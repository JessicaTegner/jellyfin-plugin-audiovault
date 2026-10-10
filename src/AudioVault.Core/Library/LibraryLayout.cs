using System.Text;
using System.Text.RegularExpressions;
using AudioVault.Core.Sources;

namespace AudioVault.Core.Library;

public enum LibraryRoot
{
    Movies,
    Shows
}

public sealed record StrmFile(LibraryRoot Root, string RelativePath, string EntryId, int? Episode);

/// <summary>Seasons whose episode list is still unknown; only a bootstrap episode 1 is written for them.</summary>
public sealed record PendingSeason(string EntryId, string SeriesRelativePath, int Season);

/// <param name="ListableSeasons">Single-part seasons, whose episode list can be looked up; the pending ones are among them.</param>
public sealed record LayoutResult(IReadOnlyList<StrmFile> Files, IReadOnlyList<PendingSeason> PendingSeasons, int ListableSeasons = 0);

public static partial class LibraryLayout
{
    public static LayoutResult Build(
        IEnumerable<SourceEntry> entries,
        LibraryOptions options,
        Func<string, EpisodeCount?> episodeCount)
    {
        var included = entries.Where(e => VersionLabeler.IsIncluded(e.Version, options)).ToList();
        var files = new List<StrmFile>();
        var pending = new List<PendingSeason>();
        var listable = 0;

        foreach (var movie in included.Where(e => e.Kind == MediaKind.Movie)
                     .GroupBy(e => (Key: NormalizeKey(e.Title), e.Year)))
        {
            var ranked = VersionLabeler.Rank(movie, options).ToList();
            var baseName = FolderName(ranked[0].Title, movie.Key.Year);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (entry, label) in VersionLabeler.LabelGroup(ranked, options))
            {
                // Labels are unique, but sanitizing can make two equal ("UK/US" and "UK:US" both give "UK US").
                var name = SafeName(label);
                if (!names.Add(name))
                {
                    name = SafeName($"{label} {entry.Id}");
                    names.Add(name);
                }

                files.Add(new StrmFile(
                    LibraryRoot.Movies,
                    Path.Combine(baseName, $"{baseName} - {name}.strm"),
                    entry.Id,
                    null));
            }
        }

        var shows = included.Where(e => e.Kind == MediaKind.Show)
            .GroupBy(e => NormalizeKey(e.Title))
            .SelectMany(SplitProductions)
            .Select(show =>
            {
                var seasons = show.GroupBy(e => e.Season ?? 1).OrderBy(g => g.Key).ToList();
                // Season years on AudioVault-style names are per season (and per part); only the year of season 1's
                // first part is the series premiere.
                var opening = seasons[0].GroupBy(e => e.Part ?? 1).MinBy(g => g.Key)!;
                var first = VersionLabeler.Rank(opening, options).First();
                return (Seasons: seasons, First: first, Name: FolderName(first.Title, seasons[0].Key == 1 ? first.Year : null));
            })
            .ToList();

        // Separate productions can still come to one folder name; the one with the lowest entry ID keeps it.
        var seriesNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (seasons, first, name) in shows.OrderBy(s => s.Seasons.SelectMany(g => g).Min(e => e.Id), StringComparer.Ordinal).ToList())
        {
            var seriesName = name;
            for (var n = 1; !seriesNames.Add(seriesName); n++)
            {
                seriesName = n == 1 ? $"{name} [{first.Id}]" : $"{name} [{first.Id}-{n}]";
            }

            foreach (var season in seasons)
            {
                var seasonDir = Path.Combine(seriesName, $"Season {season.Key:00}");
                var parts = season.GroupBy(e => e.Part ?? 1).OrderBy(g => g.Key).ToList();
                var offset = 0;
                if (parts.Count == 1)
                {
                    listable++;
                }

                foreach (var part in parts)
                {
                    // Jellyfin's multi-version support for episodes is weak, so one version per season part.
                    var best = VersionLabeler.Rank(part, options).First();
                    var count = episodeCount(best.Id);
                    if (count is null && parts.Count == 1)
                    {
                        pending.Add(new PendingSeason(best.Id, seriesName, season.Key));
                    }

                    // Requests use the archive's own numbers. A later part numbered from 1 continues the season's
                    // numbering on screen; one already numbered season-wide keeps its numbers.
                    var numbers = count?.EpisodeNumbers() ?? [1];
                    var shift = numbers.Count > 0 && numbers[0] <= offset ? offset : 0;
                    foreach (var number in numbers)
                    {
                        var ep = shift + number;
                        files.Add(new StrmFile(
                            LibraryRoot.Shows,
                            Path.Combine(seasonDir, $"{seriesName} S{season.Key:00}E{ep:00}.strm"),
                            best.Id,
                            number));
                    }

                    // Later parts continue the numbering, which is only known once this part has been downloaded.
                    if (count is not { Origin: EpisodeCountOrigin.Archive } || numbers.Count == 0)
                    {
                        break;
                    }

                    offset = shift + numbers[^1];
                }
            }
        }

        return new LayoutResult(Deduplicate(files), pending, listable);
    }

    /// <summary>
    /// Last resort for paths that still coincide (ignoring case, as some file systems do): the file of the lowest
    /// entry ID keeps its name, the others get their entry ID appended. Never drops a file.
    /// </summary>
    private static List<StrmFile> Deduplicate(List<StrmFile> files)
    {
        var comparer = StringComparer.OrdinalIgnoreCase;
        var collisions = files.GroupBy(f => (f.Root, f.RelativePath), new RootPathComparer(comparer))
            .Where(g => g.Count() > 1)
            .ToList();
        if (collisions.Count == 0)
        {
            return files;
        }

        var used = files.Select(f => (f.Root, f.RelativePath)).ToHashSet(new RootPathComparer(comparer));
        var renamed = new Dictionary<StrmFile, StrmFile>(ReferenceEqualityComparer.Instance);
        foreach (var group in collisions)
        {
            foreach (var file in group.OrderBy(f => f.EntryId, StringComparer.Ordinal).ThenBy(f => f.Episode).Skip(1))
            {
                var dir = Path.GetDirectoryName(file.RelativePath) ?? string.Empty;
                var stem = Path.GetFileNameWithoutExtension(file.RelativePath);
                var path = Path.Combine(dir, $"{stem} [{file.EntryId}].strm");
                for (var n = 2; !used.Add((file.Root, path)); n++)
                {
                    path = Path.Combine(dir, $"{stem} [{file.EntryId}-{n}].strm");
                }

                renamed[file] = file with { RelativePath = path };
            }
        }

        return [.. files.Select(f => renamed.GetValueOrDefault(f, f))];
    }

    private sealed class RootPathComparer(StringComparer paths) : IEqualityComparer<(LibraryRoot Root, string Path)>
    {
        public bool Equals((LibraryRoot Root, string Path) x, (LibraryRoot Root, string Path) y) =>
            x.Root == y.Root && paths.Equals(x.Path, y.Path);

        public int GetHashCode((LibraryRoot Root, string Path) obj) => HashCode.Combine(obj.Root, paths.GetHashCode(obj.Path));
    }

    /// <summary>
    /// Splits same-titled show entries into separate productions (e.g. The Twilight Zone 1959 and 2019). Each distinct
    /// season 1 premiere year starts a production; every season joins the one whose premiere best fits its year and
    /// number. Later parts of a season follow that season's earlier part, so a part released years later stays put.
    /// </summary>
    private static IEnumerable<IReadOnlyList<SourceEntry>> SplitProductions(IEnumerable<SourceEntry> show)
    {
        var entries = show.ToList();
        var followers = entries.Where(e => EarlierParts(entries, e).Any()).ToHashSet();
        var premieres = new List<int>();
        int? last = null;
        foreach (var year in entries
                     .Where(e => (e.Season ?? 1) == 1 && e.Year is not null && !followers.Contains(e))
                     .Select(e => e.Year!.Value).Distinct().Order())
        {
            // Versions of one season may disagree on its year by one.
            if (year - last is null or > 1)
            {
                premieres.Add(year);
            }

            last = year;
        }

        if (premieres.Count < 2)
        {
            return [entries];
        }

        var production = new Dictionary<SourceEntry, int>(ReferenceEqualityComparer.Instance);
        foreach (var entry in entries.Where(e => !followers.Contains(e)))
        {
            production[entry] = entry.Year is { } year ? BestPremiere(premieres, entry.Season ?? 1, year) : premieres[0];
        }

        // Parts in ascending order, so a part 3 can follow a part 2 that itself followed part 1.
        foreach (var entry in followers.OrderBy(e => e.Part))
        {
            var earlier = EarlierParts(entries, entry)
                .OrderBy(e => entry.Year is { } y && e.Year is { } p ? Math.Abs(y - p) : 0)
                .ThenByDescending(e => e.Part)
                .First();
            production[entry] = production[earlier];
        }

        return entries
            .GroupBy(e => production[e])
            .OrderBy(g => g.Key)
            .Select(g => (IReadOnlyList<SourceEntry>)[.. g]);
    }

    /// <summary>Earlier parts of the entry's season that it plausibly continues (released up to a few years later).</summary>
    private static IEnumerable<SourceEntry> EarlierParts(List<SourceEntry> entries, SourceEntry entry) =>
        entry.Part is not > 1
            ? []
            : entries.Where(e => (e.Season ?? 1) == (entry.Season ?? 1)
                && (e.Part ?? 1) < entry.Part
                && (entry.Year is not { } y || e.Year is not { } p || (y - p >= -1 && y - p <= MaxPartGapYears)));

    /// <summary>Parts of one season can come out a couple of years apart (Pinocchio and Friends, Yellowstone), not decades.</summary>
    private const int MaxPartGapYears = 5;

    private static int BestPremiere(List<int> premieres, int season, int year)
    {
        // Seasons usually air about a year apart, and never before the premiere (one year of slack for upload labels).
        var candidates = premieres.Where(p => p <= year + 1).ToList();
        return candidates.Count == 0
            ? premieres[0]
            : candidates.OrderBy(p => Math.Abs(year - p - (season - 1))).ThenByDescending(p => p).First();
    }

    public static string FolderName(string title, int? year)
    {
        var name = SafeName(title);
        return year is { } y ? $"{name} ({y})" : name;
    }

    public static string SafeName(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            sb.Append(c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*' || char.IsControl(c) ? ' ' : c);
        }

        var cleaned = MultiSpace().Replace(sb.ToString(), " ").Trim().Trim('.').Trim();
        return cleaned.Length == 0 ? "Untitled" : cleaned;
    }

    public static string NormalizeKey(string title)
    {
        var sb = new StringBuilder(title.Length);
        foreach (var c in title.Normalize(NormalizationForm.FormKD))
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }

        return sb.ToString();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex MultiSpace();
}
