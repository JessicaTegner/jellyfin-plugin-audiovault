using AudioVault.Core.Catalog;
using AudioVault.Core.Sources;
using Microsoft.Extensions.Logging;

namespace AudioVault.Core.Library;

public sealed record LibrarySyncResult(WriteResult Movies, WriteResult Shows, IReadOnlyList<PendingSeason> PendingSeasons, int ListableSeasons = 0)
{
    public bool Changed => Movies.Written + Movies.Deleted + Shows.Written + Shows.Deleted > 0;

    /// <summary>Files that could not be written or deleted; syncing again retries them.</summary>
    public int Failed => Movies.Failed + Shows.Failed;

    /// <summary>Files no longer wanted that the safety rail kept.</summary>
    public int Skipped => Movies.Skipped + Shows.Skipped;

    /// <summary>Old files kept until their replacements can be written (counted in <see cref="Failed"/>).</summary>
    public int Deferred => Movies.Deferred + Shows.Deferred;

    /// <summary>Whether the files now fully match the catalogue and settings: nothing failed or was held back.</summary>
    public bool Complete => Failed == 0 && Skipped == 0;
}

/// <summary>The movie and show folders can't be used as configured; nothing was written.</summary>
public sealed class LibraryConfigurationException(string message) : InvalidOperationException(message);

/// <summary>Turns the catalog into .strm files and remembers which file belongs to which entry.</summary>
public sealed class LibrarySyncService(
    CatalogStore catalog,
    EpisodeCountStore episodeCounts,
    LibraryWriter writer,
    ILogger<LibrarySyncService> logger)
{
    private readonly Lock _lock = new();
    private Dictionary<(string EntryId, int? Episode), string> _index = [];
    private Dictionary<string, (string EntryId, int? Episode)> _byPath = [];
    private Dictionary<string, string[]> _showFolders = [];
    private int _movieFolders;
    private int _episodeFiles;

    public const string AppliedFileName = ".audiovault-library-applied.json";

    /// <param name="entryOf">
    /// The entry and episode a .strm file's link plays, or null. With it, a file moving to a new path is removed only
    /// once its replacement is written, and files a settings change leaves out are removed despite the safety rail.
    /// </param>
    public LibrarySyncResult Sync(
        LibraryOptions options,
        Func<string, int?, string> streamUrl,
        Func<string, (string EntryId, int? Episode)?>? entryOf = null)
    {
        ValidateFolders(options);
        if (!catalog.Unreadable
            && !catalog.HasFullListing
            && (HasInventory(options.MoviesPath) || HasInventory(options.ShowsPath)))
        {
            // No full sync behind this catalog, yet a library made from one exists: the catalog's state was lost.
            catalog.RequireFullSync("the library exists but the catalog it was made from is missing");
        }

        if (catalog.Unreadable)
        {
            // Mirroring a catalog that only looks empty because its file was damaged would delete the whole library.
            throw new InvalidOperationException(
                "The AudioVault catalog is damaged or incomplete; the library is left as it is until the next full catalog sync.");
        }

        var entries = catalog.All().Select(e => e.Entry).ToList();
        var built = LibraryLayout.Build(entries, options, episodeCounts.Get);

        // The layout makes paths unique; should two still coincide, one file is skipped rather than the whole sync.
        var unique = built.Files
            .GroupBy(f => (f.Root, Path: f.RelativePath.ToUpperInvariant()))
            .Select(g =>
            {
                foreach (var dropped in g.Skip(1))
                {
                    logger.LogWarning("Skipping {Path} for entry {Entry}: another entry has the same file name", dropped.RelativePath, dropped.EntryId);
                }

                return g.First();
            })
            .GroupBy(f => (f.EntryId, f.Episode))
            .Select(g => g.First())
            .ToList();
        var layout = built with { Files = unique };

        var versions = entries.GroupBy(e => e.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Version, StringComparer.Ordinal);

        WriteResult Write(LibraryRoot root, string path)
        {
            var files = layout.Files.Where(f => f.Root == root).ToList();
            var applied = ReadApplied(path);
            var keys = entryOf is null
                ? null
                : new LinkKeys(
                    files.ToDictionary(f => f.RelativePath, f => Key(f.EntryId, f.Episode), StringComparer.Ordinal),
                    content => IsOwnStream(content) && entryOf(content.Trim()) is { } link ? Key(link.EntryId, link.Episode) : null,
                    key => applied is not null && applied.LeftOutBy(options, EntryOfKey(key)));
            var written = writer.Sync(
                path,
                files.ToDictionary(f => f.RelativePath, f => streamUrl(f.EntryId, f.Episode) + "\n", StringComparer.Ordinal),
                IsOwnStream,
                keys);
            if (written.Failed == 0 && written.Skipped == 0)
            {
                var held = files.Select(f => f.EntryId).Distinct().Where(versions.ContainsKey).ToDictionary(id => id, id => versions[id]);
                WriteApplied(path, AppliedLayout.Of(options, held));
            }

            return written;
        }

        var result = new LibrarySyncResult(
            Write(LibraryRoot.Movies, options.MoviesPath),
            Write(LibraryRoot.Shows, options.ShowsPath),
            layout.PendingSeasons,
            layout.ListableSeasons);

        var index = layout.Files.ToDictionary(
            f => (f.EntryId, f.Episode),
            f => Path.Combine(f.Root == LibraryRoot.Movies ? options.MoviesPath : options.ShowsPath, f.RelativePath));

        // Season and series folders, for dating Jellyfin's Season and Series items by their newest entry.
        var showFolders = layout.Files.Where(f => f.Root == LibraryRoot.Shows)
            .SelectMany(f =>
            {
                var season = Path.GetDirectoryName(index[(f.EntryId, f.Episode)])!;
                return new[] { (Folder: season, f.EntryId), (Folder: Path.GetDirectoryName(season)!, f.EntryId) };
            })
            .GroupBy(x => x.Folder, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(x => x.EntryId).Distinct().ToArray(), StringComparer.OrdinalIgnoreCase);

        lock (_lock)
        {
            _index = index;
            // Layout paths are unique ignoring case, and case-insensitive file systems may report either casing.
            _byPath = index.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.OrdinalIgnoreCase);
            _showFolders = showFolders;
            // Versions of a movie share its folder, and Jellyfin shows them as one movie.
            _movieFolders = layout.Files.Where(f => f.Root == LibraryRoot.Movies)
                .Select(f => Path.GetDirectoryName(f.RelativePath))
                .Distinct(StringComparer.Ordinal)
                .Count();
            _episodeFiles = layout.Files.Count(f => f.Root == LibraryRoot.Shows);
        }

        logger.LogInformation(
            "Library sync: movies +{MW}/-{MD}, shows +{SW}/-{SD}, {Failed} failed ({Deferred} old files kept for their replacements), {Skipped} kept by the safety rail, {Pending} seasons awaiting episode lists",
            result.Movies.Written,
            result.Movies.Deleted,
            result.Shows.Written,
            result.Shows.Deleted,
            result.Failed,
            result.Deferred,
            result.Skipped,
            result.PendingSeasons.Count);
        return result;
    }

    /// <summary>Movies and shows need separate folders; one inside the other would make each sync delete the other's files.</summary>
    public static void ValidateFolders(LibraryOptions options)
    {
        if (!Path.IsPathFullyQualified(options.MoviesPath) || !Path.IsPathFullyQualified(options.ShowsPath))
        {
            throw new LibraryConfigurationException("The movies and shows folders must be absolute paths.");
        }

        // Physical identities: a symlink or junction must not make one folder look like two.
        var movies = PathIdentity.Physical(options.MoviesPath);
        var shows = PathIdentity.Physical(options.ShowsPath);
        if (string.Equals(movies, shows, PathIdentity.Comparison))
        {
            throw new LibraryConfigurationException($"The movies and shows folders must be different (both are {movies}).");
        }

        if (PathIdentity.Overlaps(movies, shows))
        {
            throw new LibraryConfigurationException(
                $"The movies folder ({movies}) and the shows folder ({shows}) must not be inside one another.");
        }
    }

    /// <summary>Whether a library folder lists files this plugin wrote (or might have: a damaged list counts).</summary>
    private static bool HasInventory(string root)
    {
        var inventory = JsonFile.ReadContained<List<string?>>(
            Path.Combine(root, LibraryWriter.InventoryFileName), out var status, out _, setAside: false);
        return status switch
        {
            StateFileStatus.Missing => false,
            StateFileStatus.Loaded => inventory?.Any(p => p is not null) == true,
            _ => true,
        };
    }

    private static string Key(string entryId, int? episode) => episode is { } n ? $"{entryId}\u001F{n}" : entryId;

    private static string EntryOfKey(string key) => key.LastIndexOf('\u001F') is var at and >= 0 ? key[..at] : key;

    private AppliedLayout? ReadApplied(string root)
    {
        var applied = JsonFile.ReadContained<AppliedLayout>(Path.Combine(root, AppliedFileName), out var status);
        if (status is not (StateFileStatus.Loaded or StateFileStatus.Missing))
        {
            logger.LogWarning("The record of how {Root} was last written can't be read; files no longer wanted count against the safety rail", root);
        }

        return applied?.Entries is null ? null : applied;
    }

    private void WriteApplied(string root, AppliedLayout applied)
    {
        try
        {
            JsonFile.Write(Path.Combine(root, AppliedFileName), applied);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not record how {Root} was written", root);
        }
    }

    /// <summary>
    /// The settings a library folder was last completely written with, and the version of each entry it then held.
    /// A file is left out by a settings change only if, by what was known then, those settings included its entry and
    /// the new ones don't; catalogue changes alone never qualify.
    /// </summary>
    internal sealed record AppliedLayout(string PreferredLanguage, bool IncludeOtherLanguages, bool IncludeTts, Dictionary<string, AppliedVersion> Entries)
    {
        public static AppliedLayout Of(LibraryOptions options, Dictionary<string, VersionInfo> versions) => new(
            options.PreferredLanguage,
            options.IncludeOtherLanguages,
            options.IncludeTts,
            versions.ToDictionary(kv => kv.Key, kv => new AppliedVersion(kv.Value.Language, kv.Value.IsTts), StringComparer.Ordinal));

        public bool LeftOutBy(LibraryOptions desired, string entryId)
        {
            if (!Entries.TryGetValue(entryId, out var known))
            {
                return false;
            }

            var version = new VersionInfo(known.Language, known.IsTts, []);
            var before = desired with { PreferredLanguage = PreferredLanguage, IncludeOtherLanguages = IncludeOtherLanguages, IncludeTts = IncludeTts };
            return VersionLabeler.IsIncluded(version, before) && !VersionLabeler.IsIncluded(version, desired);
        }
    }

    internal sealed record AppliedVersion(string Language, bool IsTts);

    // Folders written before ownership inventories existed: adopt only links to this plugin's stream endpoint.
    private static bool IsOwnStream(string content) =>
        content.Trim() is var url && !url.Contains('\n') && url.Contains("/AudioVault/Stream/", StringComparison.Ordinal)
        && url.Contains("sig=", StringComparison.Ordinal);

    public string? FindStrmPath(string entryId, int? episode)
    {
        lock (_lock)
        {
            return _index.GetValueOrDefault((entryId, episode));
        }
    }

    public int IndexedFiles
    {
        get
        {
            lock (_lock)
            {
                return _index.Count;
            }
        }
    }

    /// <summary>From the last sync: movies (one per folder, however many versions) and episode files.</summary>
    public (int Movies, int Episodes) IndexedByRoot
    {
        get
        {
            lock (_lock)
            {
                return (_movieFolders, _episodeFiles);
            }
        }
    }

    public IReadOnlyList<string> FindStrmPaths(string entryId)
    {
        lock (_lock)
        {
            return [.. _index.Where(kv => kv.Key.EntryId == entryId).Select(kv => kv.Value)];
        }
    }

    public (string EntryId, int? Episode)? FindEntry(string strmPath)
    {
        lock (_lock)
        {
            return _byPath.TryGetValue(strmPath, out var key) ? key : null;
        }
    }

    /// <summary>
    /// The date added for a .strm file, or for a show's season or series folder (its newest entry), or null if the path
    /// isn't ours. Every episode file of a season carries the season entry's date.
    /// </summary>
    public DateTimeOffset? DateAddedFor(string path)
    {
        string[]? folderEntries;
        lock (_lock)
        {
            if (_byPath.TryGetValue(path, out var key))
            {
                return catalog.DateAdded(key.EntryId);
            }

            _showFolders.TryGetValue(Path.TrimEndingDirectorySeparator(path), out folderEntries);
        }

        return folderEntries?.Select(catalog.DateAdded).Max();
    }
}
