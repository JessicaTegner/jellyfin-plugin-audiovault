using System.Text.RegularExpressions;

namespace AudioVault.Core.Media;

/// <summary>Maps audio files from a season archive to episode numbers.</summary>
public static partial class EpisodeMapper
{
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".m4a", ".m4b", ".aac", ".wav", ".ogg", ".oga", ".opus", ".flac", ".wma", ".mka"
    };

    private static readonly Regex[] Patterns =
    [
        SxxEyy(),
        NxNN(),
        EpisodeWord(),
        EPrefix(),
        LeadingNumber(),
    ];

    public static bool IsAudio(string path) => AudioExtensions.Contains(Path.GetExtension(path));

    public static IReadOnlyDictionary<int, string> Map(IEnumerable<string> paths)
    {
        var audio = paths.Where(IsAudio)
            .Where(p => !Path.GetFileName(p).StartsWith("._", StringComparison.Ordinal))
            .ToList();
        if (audio.Count == 0)
        {
            return new Dictionary<int, string>();
        }

        foreach (var pattern in Patterns)
        {
            var mapped = new Dictionary<int, string>();
            foreach (var path in audio)
            {
                var m = pattern.Match(Path.GetFileNameWithoutExtension(path));
                if (!m.Success || !int.TryParse(m.Groups["ep"].Value, out var ep) || ep <= 0 || !mapped.TryAdd(ep, path))
                {
                    mapped = null;
                    break;
                }
            }

            if (mapped is not null)
            {
                return mapped;
            }
        }

        return audio.Order(NaturalComparer.Instance)
            .Select((p, i) => (p, i))
            .ToDictionary(x => x.i + 1, x => x.p);
    }

    [GeneratedRegex(@"S\d{1,2}\s*E(?<ep>\d{1,3})", RegexOptions.IgnoreCase)]
    private static partial Regex SxxEyy();

    [GeneratedRegex(@"\b\d{1,2}x(?<ep>\d{1,3})\b", RegexOptions.IgnoreCase)]
    private static partial Regex NxNN();

    [GeneratedRegex(@"\b(?:episode|ep\.?)\s*(?<ep>\d{1,3})\b", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodeWord();

    [GeneratedRegex(@"(?:^|[\s._-])E(?<ep>\d{1,3})\b", RegexOptions.IgnoreCase)]
    private static partial Regex EPrefix();

    [GeneratedRegex(@"^(?<ep>\d{1,3})(?:[\s._-]|$)")]
    private static partial Regex LeadingNumber();

    private sealed partial class NaturalComparer : IComparer<string>
    {
        public static readonly NaturalComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            var a = Chunks().Matches(x ?? string.Empty);
            var b = Chunks().Matches(y ?? string.Empty);
            for (var i = 0; i < Math.Min(a.Count, b.Count); i++)
            {
                var (ca, cb) = (a[i].Value, b[i].Value);
                int c = char.IsDigit(ca[0]) && char.IsDigit(cb[0]) && long.TryParse(ca, out var na) && long.TryParse(cb, out var nb)
                    ? na.CompareTo(nb)
                    : string.Compare(ca, cb, StringComparison.OrdinalIgnoreCase);
                if (c != 0)
                {
                    return c;
                }
            }

            return a.Count.CompareTo(b.Count);
        }

        [GeneratedRegex(@"\d+|\D+")]
        private static partial Regex Chunks();
    }
}
