using AudioVault.Core.Sources;

namespace AudioVault.Core.Library;

public static class VersionLabeler
{
    public static bool IsIncluded(VersionInfo version, LibraryOptions options) =>
        (options.IncludeTts || !version.IsTts)
        && (options.IncludeOtherLanguages || IsPreferred(version, options));

    /// <summary>Best first: preferred language, human narration, fewest qualifiers, newest upload.</summary>
    public static IEnumerable<SourceEntry> Rank(IEnumerable<SourceEntry> entries, LibraryOptions options) =>
        entries
            .OrderBy(e => IsPreferred(e.Version, options) ? 0 : 1)
            .ThenBy(e => e.Version.IsTts ? 1 : 0)
            .ThenBy(e => e.Version.Tags.Count)
            .ThenByDescending(e => long.TryParse(e.Id, out var n) ? n : 0)
            .ThenBy(e => e.Id, StringComparer.Ordinal);

    public static string Label(VersionInfo version, LibraryOptions options)
    {
        var parts = new List<string>();
        if (!IsPreferred(version, options))
        {
            parts.Add(version.Language);
        }

        parts.AddRange(version.Tags);
        if (version.IsTts)
        {
            parts.Add("TTS");
        }

        return parts.Count == 0 ? version.Language : string.Join(' ', parts);
    }

    /// <summary>Assigns a label per entry, made unique within the group. Input order is kept.</summary>
    public static IReadOnlyList<(SourceEntry Entry, string Label)> LabelGroup(
        IReadOnlyList<SourceEntry> ranked,
        LibraryOptions options)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<(SourceEntry, string)>(ranked.Count);
        foreach (var entry in ranked)
        {
            var label = Label(entry.Version, options);
            if (!used.Add(label))
            {
                label = $"{label} {entry.Id}";
                used.Add(label);
            }

            result.Add((entry, label));
        }

        return result;
    }

    private static bool IsPreferred(VersionInfo version, LibraryOptions options) =>
        string.Equals(version.Language, options.PreferredLanguage, StringComparison.OrdinalIgnoreCase);
}
