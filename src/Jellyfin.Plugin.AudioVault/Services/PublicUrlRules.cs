using System.Net;
using System.Net.Sockets;

namespace Jellyfin.Plugin.AudioVault.Services;

public enum PublicUrlSource
{
    Plugin,
    Published,
    Detected,
    Fallback,
}

/// <summary>Jellyfin's networking settings that decide its published server URI, as plain values.</summary>
/// <param name="StartupUrl">The PublishedServerUrl startup setting (environment variable or command line).</param>
/// <param name="BySubnet">NetworkConfiguration.PublishedServerUriBySubnet entries, e.g. "all=https://jf.example.com".</param>
public sealed record PublishedUriSettings(
    string? StartupUrl,
    IReadOnlyList<string> BySubnet,
    string? BaseUrl,
    bool ListenWithHttps,
    int HttpPort,
    int HttpsPort);

public sealed record PublicUrlChoice(string Url, PublicUrlSource Source, string? Problem)
{
    public string SourceText => Source switch
    {
        PublicUrlSource.Plugin => "set in this plugin",
        PublicUrlSource.Published => "Jellyfin's published server URI",
        PublicUrlSource.Detected => "detected from the address you used to open this page",
        _ => "fallback",
    };
}

/// <summary>Picks the address written into .strm files: one URL every device and the server itself can use.</summary>
public static class PublicUrlRules
{
    private const string FixHint =
        " Open this page via the address you normally use for Jellyfin, or set Jellyfin's Published Server URI"
        + " (Dashboard → Networking), or fill in the Public server URL field.";

    /// <summary>
    /// Order: the plugin's own field; a published URI that applies to every client (startup setting, "all=",
    /// "external="); the address the admin opened the settings page with; a published URI for some subnets only;
    /// Jellyfin's local address, with a warning.
    /// </summary>
    public static PublicUrlChoice Choose(string? pluginUrl, PublishedUriSettings published, string? detected, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(pluginUrl))
        {
            // A query or fragment would end up in front of the stream path; problems are reported, links still use the base.
            var problem = Problem(pluginUrl);
            var url = Normalize(pluginUrl) ?? pluginUrl.Trim().TrimEnd('/');
            return new PublicUrlChoice(
                url,
                PublicUrlSource.Plugin,
                problem is null ? null : $"The public server URL {problem}. Enter the address you use to open Jellyfin, for example http://my-server:8096, or leave the field empty to detect it automatically.");
        }

        var (everyone, someSubnets) = PublishedUrls(published);
        if (everyone.FirstOrDefault(u => Problem(u) is null) is { } forEveryone)
        {
            return new PublicUrlChoice(forEveryone, PublicUrlSource.Published, null);
        }

        if (Normalize(detected) is { } found && Problem(found) is null)
        {
            return new PublicUrlChoice(found, PublicUrlSource.Detected, null);
        }

        if (someSubnets.FirstOrDefault(u => Problem(u) is null) is { } forSome)
        {
            return new PublicUrlChoice(forSome, PublicUrlSource.Published, null);
        }

        var local = fallback.TrimEnd('/');
        return new PublicUrlChoice(
            local,
            PublicUrlSource.Fallback,
            $"Couldn't detect an address other devices can use, so playback links use {local}, which players on other devices may not reach." + FixHint);
    }

    /// <summary>Why the URL can't be used by other devices (completes "The public server URL …"), or null if it can.</summary>
    public static string? Problem(string? url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.Host.Length == 0)
        {
            return "is not a valid http or https address";
        }

        if (uri.IsLoopback || uri.Host is "0.0.0.0" or "[::]")
        {
            return $"uses {uri.Host}, which on every other device means that device itself, not this server";
        }

        if (uri.UserInfo.Length > 0)
        {
            return "contains a user name or password, which must not be written into the library files";
        }

        if (uri.Query.Length > 0 || uri.Fragment.Length > 0 || url!.Contains('#', StringComparison.Ordinal) || url.Contains('?', StringComparison.Ordinal))
        {
            return "contains a query (?) or fragment (#); enter only the address, with Jellyfin's base path if it has one";
        }

        return null;
    }

    /// <summary>
    /// Advice about an address that works, but only for some devices: a bare machine name, a .local name, a Tailscale
    /// MagicDNS name, or a private network address when people outside this network may play. Null when none applies.
    /// Suggests the first alternative without such a concern.
    /// </summary>
    /// <param name="othersMayPlay">Whether people other than the admin may play, e.g. there are several Jellyfin users.</param>
    public static string? Advice(string? url, bool othersMayPlay, IEnumerable<string?> alternatives)
    {
        if (Problem(url) is not null || Normalize(url) is not { } current || Concern(new Uri(current), othersMayPlay) is not { } concern)
        {
            return null;
        }

        var better = alternatives
            .Select(Normalize)
            .OfType<string>()
            .FirstOrDefault(a => !string.Equals(a, current, StringComparison.OrdinalIgnoreCase) && Problem(a) is null && Concern(new Uri(a), othersMayPlay) is null);
        return better is null ? concern.Text + " " + concern.Hint : $"{concern.Text} {better} may work on more devices.";
    }

    private static (string Text, string Hint)? Concern(Uri uri, bool othersMayPlay)
    {
        var host = uri.Host.TrimEnd('.').ToLowerInvariant();
        if (uri.HostNameType == UriHostNameType.Dns)
        {
            if (host.EndsWith(".ts.net", StringComparison.Ordinal))
            {
                return (
                    $"{host} is a Tailscale MagicDNS name. Devices outside your tailnet may not reach it, and devices from other tailnets this machine is shared with often can't resolve it.",
                    "For them, this machine's Tailscale address (100.x.y.z) or a published domain works better.");
            }

            if (host.EndsWith(".local", StringComparison.Ordinal))
            {
                return (
                    $"{host} is a .local name. Devices outside this network may not reach it, and many phones, TVs and streaming boxes can't resolve it even on the same network.",
                    "The server's IP address or a published domain works on more devices.");
            }

            if (!host.Contains('.', StringComparison.Ordinal))
            {
                return (
                    $"{host} is a bare machine name. Devices outside this network may not reach it, and on it only devices whose network knows that name can.",
                    "The server's IP address or a published domain works on more devices.");
            }

            return null;
        }

        return othersMayPlay && IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && IsPrivate(ip)
            ? (
                $"{uri.Host} is a private network address. Devices outside this network, such as a phone on mobile data or other users at home, may not reach it.",
                "A published domain (for example through a reverse proxy) or a VPN such as Tailscale works from anywhere.")
            : null;
    }

    /// <summary>Home and office network ranges (RFC 1918, link-local, IPv6 unique-local); not Tailscale's 100.64.0.0/10.</summary>
    public static bool IsPrivate(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return ip.IsIPv6LinkLocal || ip.IsIPv6UniqueLocal || ip.IsIPv6SiteLocal;
        }

        var b = ip.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
    }

    /// <summary>Tailscale's addresses, 100.64.0.0/10.</summary>
    public static bool IsTailscale(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        var b = ip.GetAddressBytes();
        return ip.AddressFamily == AddressFamily.InterNetwork && b[0] == 100 && (b[1] & 0xC0) == 64;
    }

    /// <summary>
    /// The same address with its MagicDNS name replaced by the Tailscale IP it resolves to; null unless it is a plain
    /// http *.ts.net address (an https certificate is only valid for the name).
    /// </summary>
    public static string? TailscaleAlternative(string? url, IEnumerable<IPAddress> resolved)
    {
        if (Normalize(url) is not { } current)
        {
            return null;
        }

        var uri = new Uri(current);
        if (uri.Scheme != "http" || !uri.Host.TrimEnd('.').EndsWith(".ts.net", StringComparison.OrdinalIgnoreCase)
            || resolved.FirstOrDefault(IsTailscale) is not { } ip)
        {
            return null;
        }

        return Normalize(new UriBuilder(uri) { Host = ip.MapToIPv4().ToString() }.Uri.ToString());
    }

    /// <summary>Scheme, host, port and path of an absolute http(s) URL, without user info, query, fragment, repeated or trailing slashes; null otherwise.</summary>
    public static string? Normalize(string? url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.Host.Length == 0)
        {
            return null;
        }

        var path = string.Join('/', uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries));
        return uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped) + (path.Length == 0 ? string.Empty : "/" + path);
    }

    /// <summary>Whether a newly detected address should replace the stored one. Never trades https or a domain name for less.</summary>
    public static bool ShouldReplaceDetected(string? stored, string? candidate)
    {
        var next = Normalize(candidate);
        if (next is null || Problem(next) is not null)
        {
            return false;
        }

        var current = Normalize(stored);
        if (current is null || Problem(current) is not null)
        {
            return true;
        }

        return !string.Equals(current, next, StringComparison.OrdinalIgnoreCase) && Rank(next) >= Rank(current);
    }

    /// <summary>https and a DNS name are more likely to work from every device than http and an IP address.</summary>
    public static int Rank(string url)
    {
        var uri = new Uri(url);
        return (uri.Scheme == "https" ? 2 : 0) + (uri.HostNameType == UriHostNameType.Dns && uri.Host.Contains('.', StringComparison.Ordinal) ? 1 : 0);
    }

    /// <summary>
    /// Published URIs as Jellyfin's NetworkManager reads them: the startup setting replaces the list, "all=" replaces
    /// every other entry, and an unparsable entry stops the list. Full URLs are used as written (Jellyfin doesn't add
    /// its base URL to them); a bare host gets Jellyfin's scheme, port and base URL, as GetLocalApiUrl builds it.
    /// </summary>
    public static (List<string> Everyone, List<string> SomeSubnets) PublishedUrls(PublishedUriSettings s)
    {
        var everyone = new List<string>();
        var some = new List<string>();
        if (!string.IsNullOrWhiteSpace(s.StartupUrl))
        {
            AddBuilt(everyone, s.StartupUrl, s);
            return (everyone, some);
        }

        foreach (var entry in s.BySubnet)
        {
            var parts = entry.Split('=');
            if (parts.Length != 2)
            {
                break;
            }

            var key = parts[0].Trim();
            if (key.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                everyone.Clear();
                some.Clear();
                AddBuilt(everyone, parts[1], s);
                break;
            }

            AddBuilt(key.Equals("external", StringComparison.OrdinalIgnoreCase) ? everyone : some, parts[1], s);
        }

        return (everyone, some);
    }

    private static void AddBuilt(List<string> list, string value, PublishedUriSettings s)
    {
        if (Build(value.Trim(), s) is { } url)
        {
            list.Add(url);
        }
    }

    private static string? Build(string value, PublishedUriSettings s)
    {
        if (value.Length == 0)
        {
            return null;
        }

        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return value.TrimEnd('/');
        }

        if (!Uri.TryCreate("any://" + value, UriKind.Absolute, out var parsed) || parsed.Host.Length == 0)
        {
            return null;
        }

        var scheme = s.ListenWithHttps ? "https" : "http";
        var port = parsed.Port != -1 ? parsed.Port : s.ListenWithHttps ? s.HttpsPort : s.HttpPort;
        var baseUrl = (s.BaseUrl ?? string.Empty).Trim('/');
        return new UriBuilder(scheme, parsed.DnsSafeHost, port, baseUrl.Length == 0 ? string.Empty : "/" + baseUrl)
            .ToString()
            .TrimEnd('/');
    }
}
