using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.AudioVault.Services;

/// <summary>A value the report must never show, and what to write instead (e.g. "user name").</summary>
/// <param name="IsFolder">Whatever follows the folder in a path (titles, file names) goes too.</param>
public sealed record KnownSecret(string? Value, string Label, bool IsFolder = false);

/// <summary>
/// Removes personal and secret details from text for the diagnostic report: URLs and emails, then known titles, values
/// and folders, then anything that looks like a path, token, IP address or host name. It errs towards removing too much.
/// No Jellyfin types.
/// </summary>
public sealed partial class Redactor
{
    // The plugin's own wording that is quoted in messages; anything else in quotes may be a title or a name.
    private static readonly HashSet<string> SafeQuoted = new(StringComparer.OrdinalIgnoreCase)
    {
        "AudioVault account", "Run full catalog sync now", "Sync recent uploads now", "Check AudioVault connection", "remember me",
    };

    // Where a path's name can't be told apart from the prose after it, everything up to the end of the clause goes.
    // An apostrophe inside a word ("Bob's") doesn't end it; a quote does.
    private const string Rest = @"(?:[^""'“”‘’«»<>|;\r\n\t:]|(?<=\w)['’](?=\w)|:(?!\s|$))*";
    private const string NotInPath = @"\s""'“”‘’«»<>|;:/\\";

    // Titles shorter than this are only matched in their exact case, so "It" doesn't take every "it".
    private const int CaseInsensitiveTitle = 4;

    private readonly List<(Regex Pattern, string Label)> _folders;
    private readonly List<(Regex Pattern, string Label)> _known;
    private readonly HashSet<string> _titles = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _shortTitles = new(StringComparer.Ordinal);
    private readonly int _longestTitle;

    public Redactor(IEnumerable<KnownSecret> known, IEnumerable<string?>? titles = null)
    {
        var values = new Dictionary<string, (string Label, bool Folder)>(StringComparer.OrdinalIgnoreCase);
        void Add(string? value, string label, bool folder = false)
        {
            var v = value?.Trim().TrimEnd('/', '\\');
            if (!string.IsNullOrEmpty(v) && v.Length >= 2)
            {
                values.TryAdd(v, (label, folder));
            }
        }

        foreach (var k in known)
        {
            Add(k.Value, k.Label, k.IsFolder);
            if (Uri.TryCreate(k.Value?.Trim(), UriKind.Absolute, out var uri) && uri.Host.Length > 0)
            {
                Add(uri.Host, k.Label + " host");
            }
        }

        foreach (var title in titles ?? [])
        {
            var t = title?.Trim();
            if (string.IsNullOrEmpty(t) || t.Length < 2)
            {
                continue;
            }

            (t.Length < CaseInsensitiveTitle ? _shortTitles : _titles).Add(t);
            _longestTitle = Math.Max(_longestTitle, t.Length);
        }

        // Longest first, so a folder is replaced before the folder that contains it.
        List<(Regex, string)> Patterns(bool folders) =>
        [
            .. values
                .Where(v => v.Value.Folder == folders)
                .OrderByDescending(v => v.Key.Length)
                .Select(v => (
                    new Regex(
                        @"(?<![\w])" + Regex.Escape(v.Key) + (folders ? @"(?:[/\\]" + Rest + ")?" : string.Empty) + @"(?![\w])",
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
                    $"[{v.Value.Label}]")),
        ];
        _folders = Patterns(folders: true);
        _known = Patterns(folders: false);
    }

    /// <param name="titles">
    /// False for text the plugin wrote itself (checklist details, task names): it never names a title, and many titles
    /// are ordinary words ("After", "Playback") that would garble it.
    /// </param>
    public string Scrub(string? text, bool titles = true)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var s = UrlPattern().Replace(text, m => "[" + DescribeUrl(m.Value.TrimEnd('.')) + " URL]" + TrailingStop(m.Value));
        s = EmailPattern().Replace(s, "[email]");

        // Titles go whole before anything else can split one.
        if (titles)
        {
            s = ReplaceTitles(s);
        }
        foreach (var (pattern, label) in _known)
        {
            s = pattern.Replace(s, label);
        }

        foreach (var (pattern, label) in _folders)
        {
            s = pattern.Replace(s, m => label + TrailingStop(m.Value));
        }

        s = WindowsPathPattern().Replace(s, m => "[path]" + TrailingStop(m.Value));
        s = UnixPathPattern().Replace(s, m => "[path]" + TrailingStop(m.Value));
        s = RelativePathPattern().Replace(s, m => "[path]" + TrailingStop(m.Value));
        s = SecretPattern().Replace(s, m => m.Groups["key"].Value + "=[redacted]");
        s = BearerPattern().Replace(s, m => m.Groups["scheme"].Value + " [redacted]");
        s = Ipv4Pattern().Replace(s, m => IPAddress.TryParse(m.Value, out var ip) ? $"[{Kind(ip)} IP]" : m.Value);
        s = Ipv6Pattern().Replace(s, m => m.Value.Contains("::", StringComparison.Ordinal) || m.Value.Count(c => c == ':') == 7
            ? IPAddress.TryParse(m.Value, out var ip) ? $"[{Kind(ip)} IP]" : m.Value
            : m.Value);
        s = TokenPattern().Replace(s, "[token]");
        s = HostPattern().Replace(s, m => m.Value.Equals("audiovault.net", StringComparison.OrdinalIgnoreCase) ? m.Value : "[host]");
        s = QuotedPattern().Replace(s, m => SafeQuoted.Contains(m.Groups["inner"].Value) || IsLabel(m.Groups["inner"].Value) ? m.Value : m.Groups["open"].Value + "[name]" + m.Groups["close"].Value);
        return s;
    }

    /// <summary>The longest known title starting at each word boundary; a lookup per boundary, as there may be thousands.</summary>
    private string ReplaceTitles(string s)
    {
        if (_longestTitle == 0)
        {
            return s;
        }

        var titles = _titles.GetAlternateLookup<ReadOnlySpan<char>>();
        var shortTitles = _shortTitles.GetAlternateLookup<ReadOnlySpan<char>>();
        bool Boundary(int i) => i == 0 || i == s.Length || !(char.IsLetterOrDigit(s[i - 1]) && char.IsLetterOrDigit(s[i]));

        var sb = new StringBuilder(s.Length);
        var at = 0;
        while (at < s.Length)
        {
            var end = -1;
            if (Boundary(at) && !char.IsWhiteSpace(s[at]))
            {
                for (var e = Math.Min(s.Length, at + _longestTitle); e > at + 1; e--)
                {
                    if (Boundary(e) && !char.IsWhiteSpace(s[e - 1]))
                    {
                        var candidate = s.AsSpan(at, e - at);
                        if (titles.Contains(candidate) || shortTitles.Contains(candidate))
                        {
                            end = e;
                            break;
                        }
                    }
                }
            }

            if (end < 0)
            {
                sb.Append(s[at++]);
            }
            else
            {
                sb.Append("[title]");
                at = end;
            }
        }

        return sb.ToString();
    }

    // Already replaced, e.g. "[title]".
    private static bool IsLabel(string s) => s.Length > 2 && s[0] == '[' && s[^1] == ']' && s.IndexOf(']', StringComparison.Ordinal) == s.Length - 1;

    // A sentence's full stop swallowed with a path is put back.
    private static string TrailingStop(string match) => match.EndsWith('.') ? "." : string.Empty;

    /// <summary>Only the scheme and the kind of address, e.g. "https, public name" or "http, Tailscale IP".</summary>
    public static string DescribeUrl(string? url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) || uri.Host.Length == 0)
        {
            return string.IsNullOrWhiteSpace(url) ? "none" : "not a valid address";
        }

        if (uri.Host.Equals("audiovault.net", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".audiovault.net", StringComparison.OrdinalIgnoreCase))
        {
            return uri.Scheme + ", audiovault.net";
        }

        return uri.Scheme + ", " + HostKind(uri.Host);
    }

    /// <summary>localhost, private network, Tailscale or public, for an IP address or host name.</summary>
    public static string HostKind(string host)
    {
        var h = host.Trim('[', ']').TrimEnd('.');
        if (IPAddress.TryParse(h, out var ip))
        {
            return Kind(ip) + " IP";
        }

        if (h.Equals("localhost", StringComparison.OrdinalIgnoreCase) || h.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            return "localhost";
        }

        if (h.EndsWith(".ts.net", StringComparison.OrdinalIgnoreCase))
        {
            return "Tailscale name";
        }

        return !h.Contains('.', StringComparison.Ordinal)
            || new[] { ".local", ".lan", ".home", ".internal", ".home.arpa", ".localdomain" }.Any(suffix => h.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            ? "private network name"
            : "public name";
    }

    private static string Kind(IPAddress ip) =>
        IPAddress.IsLoopback(ip) ? "localhost"
        : PublicUrlRules.IsTailscale(ip) || IsTailscaleV6(ip) ? "Tailscale"
        : PublicUrlRules.IsPrivate(ip) ? "private network"
        : "public";

    /// <summary>Tailscale's IPv6 range, fd7a:115c:a1e0::/48.</summary>
    private static bool IsTailscaleV6(IPAddress ip)
    {
        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            return false;
        }

        var b = ip.GetAddressBytes();
        return b[0] == 0xfd && b[1] == 0x7a && b[2] == 0x11 && b[3] == 0x5c && b[4] == 0xa1 && b[5] == 0xe0;
    }

    [GeneratedRegex(@"\b[a-z][a-z0-9+.\-]*://(?:\[[0-9a-f:.]+\]|[^\s""'<>()\[\]/])[^\s""'<>()\[\]]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlPattern();

    [GeneratedRegex(@"[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EmailPattern();

    [GeneratedRegex(
        @"(?<key>\b(?:api[_\-]?key|access[_\-]?token|refresh[_\-]?token|token|password|passwd|pwd|secret|sig|signature|ticket|cookie|remember[_\-]?\w*|session[_\-]?id|x-emby-token|x-mediabrowser-token|av_t))\s*[=:]\s*""?[^\s&;,""]+""?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretPattern();

    [GeneratedRegex(@"(?<scheme>\b(?:Bearer|Basic))\s+[A-Za-z0-9._~+/=\-]{8,}", RegexOptions.CultureInvariant)]
    private static partial Regex BearerPattern();

    [GeneratedRegex(@"(?<![\d.])(?:\d{1,3}\.){3}\d{1,3}(?![\d.])", RegexOptions.CultureInvariant)]
    private static partial Regex Ipv4Pattern();

    [GeneratedRegex(@"(?<![\w:])(?:[0-9a-f]{0,4}:){2,7}[0-9a-f]{0,4}(?![\w:])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Ipv6Pattern();

    [GeneratedRegex(@"(?<![\w])(?:[A-Za-z]:\\|\\\\)" + Rest, RegexOptions.CultureInvariant)]
    private static partial Regex WindowsPathPattern();

    // Two or more folders deep; the plugin's own /AudioVault/ endpoints are kept.
    [GeneratedRegex(@"(?<![\w.~/:\]\[])~?/(?!AudioVault/)(?=[^""'“”‘’«»<>|;\r\n\t:/]*/)" + Rest, RegexOptions.CultureInvariant)]
    private static partial Regex UnixPathPattern();

    // Anything with a slash inside a word ("../show/ep1.mp3", "Show S01/ep1.mp3"), with the capitalised words before it.
    [GeneratedRegex(
        @"(?<=^|[\s""'“‘«(=])(?:(?-i:[A-Z0-9\[])[^" + NotInPath + @"]*[ ])*[^" + NotInPath + @"]+[/\\](?=[^\s/\\]{2})" + Rest,
        RegexOptions.CultureInvariant)]
    private static partial Regex RelativePathPattern();

    [GeneratedRegex(@"(?<![\w\-])[A-Za-z0-9_\-+/]{32,}={0,2}(?![\w\-])", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();

    // Any dotted name ending in letters, with or without a port; file names go too.
    [GeneratedRegex(
        @"(?<![\w.\-])(?:[a-z0-9](?:[a-z0-9\-]{0,61}[a-z0-9])?\.)+[a-z][a-z0-9\-]*[a-z0-9](?::\d{1,5})?(?![\w\-])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HostPattern();

    [GeneratedRegex(@"(?<![\w])(?<open>[""'“‘«])(?<inner>[^\s""'“”‘’«»][^""“”‘’«»\r\n]{0,199}?)(?<close>[""'”’»])(?![\w])", RegexOptions.CultureInvariant)]
    private static partial Regex QuotedPattern();
}
