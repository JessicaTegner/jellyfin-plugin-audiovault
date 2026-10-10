using Microsoft.Extensions.Logging;

namespace AudioVault.Core.Media;

public sealed class InsufficientDiskSpaceException(string message) : Sources.SourceException(message);

/// <summary>The cache folder already holds a folder by an entry's name that the plugin didn't create.</summary>
public sealed class CacheFolderConflictException(string message) : Sources.SourceException(message);

/// <summary>
/// On-disk layout: {root}/{entryId}/ holding the source audio or archive and converted .mp4 files. Only folders this
/// plugin made are ever deleted: the root carries <see cref="OwnerMarkerName"/> and each entry folder
/// <see cref="EntryMarkerName"/>. A non-empty folder without the root marker is used but never cleaned.
/// </summary>
public sealed partial class CacheStore(Func<string> root, ILogger<CacheStore> logger)
{
    /// <summary>Probe placeholders live here; never evicted.</summary>
    public const string PlaceholderDirName = "placeholders";

    /// <summary>Marks a cache folder as this plugin's (created only in a new or empty folder).</summary>
    public const string OwnerMarkerName = ".audiovault-cache";

    /// <summary>Marks an entry folder the plugin created; only these are evicted or cleaned up.</summary>
    public const string EntryMarkerName = ".audiovault-entry";

    private static readonly TimeSpan UsageRefreshInterval = TimeSpan.FromSeconds(30);
    private static readonly EnumerationOptions AllFiles = new() { RecurseSubdirectories = true, IgnoreInaccessible = true };
    private static readonly EnumerationOptions TopFiles = new() { IgnoreInaccessible = true };

    // Hidden files count too, and anything unreadable fails the check instead of being skipped.
    private static readonly EnumerationOptions EverythingBelow = new() { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false };

    private string? _ownedRoot;
    private long _cachedTotal;
    private long _usageRefreshedAt = long.MinValue / 2;
    private int _usageRefreshing;

    /// <summary>Free bytes on the volume holding a directory, or null if unknown. Replaceable for tests.</summary>
    public Func<string, long?> FreeSpace { get; set; } = DefaultFreeSpace;

    public string Root => root();

    public string EntryDir(string entryId) => Path.Combine(root(), SafeId(entryId));

    public string FinalPath(string entryId, int? episode) => Path.Combine(EntryDir(entryId), Key(episode) + ".mp4");

    public string LivePath(string entryId, int? episode) => Path.Combine(EntryDir(entryId), Key(episode) + ".live.mp4");

    public string RemuxPath(string entryId, int? episode) => Path.Combine(EntryDir(entryId), Key(episode) + ".remux.mp4");

    public string PlaceholderPath() => Path.Combine(root(), "placeholder.mp4");

    public string PlaceholderPath(int seconds) => Path.Combine(root(), PlaceholderDirName, $"{seconds}.mp4");

    public string SourcePath(string entryId) => Path.Combine(EntryDir(entryId), "source.audio");

    public string ArchivePath(string entryId) => Path.Combine(EntryDir(entryId), "source.archive");

    public string ExtractDir(string entryId) => Path.Combine(EntryDir(entryId), "extracted");

    public string EpisodeMapPath(string entryId) => Path.Combine(EntryDir(entryId), "episodes.json");

    /// <summary>
    /// Why the plugin won't clean up or evict anything in the cache folder, or null if it may (including when the
    /// folder doesn't exist yet). Never throws and never changes the disk.
    /// </summary>
    public string? OwnershipProblem
    {
        get
        {
            string dir;
            try
            {
                dir = root();
                if (_ownedRoot == dir || File.Exists(Path.Combine(dir, OwnerMarkerName)) || !Directory.Exists(dir)
                    || !Directory.EnumerateFileSystemEntries(dir).Any() || IsLegacyCache(dir))
                {
                    return null;
                }
            }
            catch (Exception ex)
            {
                return $"The AudioVault cache folder can't be read ({ex.Message}).";
            }

            return $"The AudioVault cache folder {dir} already holds other files, so AudioVault won't delete anything "
                + "in it (no cache size limit or clean-up). Choose an empty folder for the cache.";
        }
    }

    /// <summary>Creates the cache folder if needed and claims it when it is new or empty. Never throws.</summary>
    public bool EnsureRoot()
    {
        try
        {
            var dir = root();
            if (_ownedRoot == dir)
            {
                return true;
            }

            var marker = Path.Combine(dir, OwnerMarkerName);
            Directory.CreateDirectory(dir);
            if (!File.Exists(marker))
            {
                var legacy = false;
                if (Directory.EnumerateFileSystemEntries(dir).Any() && !File.Exists(marker))
                {
                    if (!IsLegacyCache(dir))
                    {
                        return false;
                    }

                    legacy = true;
                }

                if (legacy)
                {
                    // A cache written before the markers existed: adopt the entry folders it is made of.
                    foreach (var entry in Directory.EnumerateDirectories(dir).Where(d => IsLegacyEntry(d) == true))
                    {
                        MarkEntry(entry);
                    }

                    logger.LogInformation("Adopted the existing AudioVault cache in {Root}", dir);
                }

                File.WriteAllText(marker, "Managed by the Jellyfin AudioVault plugin. Do not store other files here.\n");
            }

            _ownedRoot = dir;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            logger.LogWarning(ex, "Could not set up the AudioVault cache folder");
            return false;
        }
    }

    /// <summary>
    /// Creates an entry's folder, marked as the plugin's when the cache folder is. Throws
    /// <see cref="CacheFolderConflictException"/> rather than use (and later clean up or evict) an existing folder
    /// of that name holding anything the plugin wouldn't have written.
    /// </summary>
    public string EnsureEntryDir(string entryId)
    {
        var dir = EntryDir(entryId);
        var owned = EnsureRoot();
        if (Directory.Exists(dir) && EntryProblem(dir) is not null)
        {
            throw new CacheFolderConflictException(
                $"The AudioVault cache folder already has a folder {dir} that AudioVault didn't create, so this title "
                + "can't be stored. Move that folder out of the cache folder and try again.");
        }

        Directory.CreateDirectory(dir);
        if (owned)
        {
            MarkEntry(dir);
        }

        return dir;
    }

    /// <summary>
    /// Whether an entry's existing folder may be read, written and cleaned up: marked as the plugin's, or holding
    /// only files the plugin names (an older version's). False when it is missing, a link, or holds anything else.
    /// Never throws and never changes the disk.
    /// </summary>
    public bool OwnsEntry(string entryId)
    {
        try
        {
            var dir = EntryDir(entryId);
            return Directory.Exists(dir) && EntryProblem(dir) is null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    // Why an existing entry folder isn't the plugin's, or null if it is.
    private string? EntryProblem(string dir)
    {
        if (new DirectoryInfo(dir).LinkTarget is not null)
        {
            // A link could point anywhere; writing or evicting through it would touch files outside the cache.
            return "is a link";
        }

        return File.Exists(Path.Combine(dir, EntryMarkerName)) || IsLegacyEntry(dir) != false ? null : "holds other files";
    }

    /// <summary>A copy whose paths stay under the current root even if the configured folder changes later.</summary>
    public CacheStore Pinned()
    {
        var fixedRoot = root();
        return new CacheStore(() => fixedRoot, logger) { FreeSpace = FreeSpace };
    }

    public bool TryGetReady(string entryId, int? episode, out string path)
    {
        path = FinalPath(entryId, episode);
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            File.SetLastWriteTimeUtc(EntryDir(entryId), DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return true;
    }

    public long TotalBytes()
    {
        var total = Directory.Exists(root()) ? SizeOf(new DirectoryInfo(root())) : 0;
        Interlocked.Exchange(ref _cachedTotal, total);
        Interlocked.Exchange(ref _usageRefreshedAt, Environment.TickCount64);
        return total;
    }

    /// <summary>
    /// Cache usage as of the last count (at most <see cref="UsageRefreshInterval"/> old; refreshed in the
    /// background, so the first read can be 0). Never touches the disk on the caller's thread and never throws.
    /// </summary>
    public long CachedTotalBytes
    {
        get
        {
            if (Environment.TickCount64 - Interlocked.Read(ref _usageRefreshedAt) > UsageRefreshInterval.TotalMilliseconds
                && Interlocked.CompareExchange(ref _usageRefreshing, 1, 0) == 0)
            {
                _ = Task.Run(() =>
                {
                    try
                    {
                        TotalBytes();
                    }
                    catch (Exception ex)
                    {
                        logger.LogDebug(ex, "Counting cache usage failed");
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _usageRefreshing, 0);
                    }
                });
            }

            return Interlocked.Read(ref _cachedTotal);
        }
    }

    /// <summary>Throws unless the cache volume has at least <paramref name="minFreeBytes"/> plus <paramref name="needed"/> free.</summary>
    public void EnsureFreeSpace(long minFreeBytes, long needed = 0) => EnsureFreeSpace(minFreeBytes, needed, null);

    /// <param name="reclaim">When space is short, offered evictable entries least recently used first (as for
    /// <see cref="EnforceLimit(long, Func{string, Func{bool}, bool})"/>) until enough is free.</param>
    public void EnsureFreeSpace(long minFreeBytes, long needed, Func<string, Func<bool>, bool>? reclaim)
    {
        if (minFreeBytes <= 0 && needed <= 0)
        {
            return;
        }

        var dir = root();
        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        var required = minFreeBytes + needed;
        if (reclaim is not null && FreeSpace(dir) is { } before && before < required && OwnershipProblem is null)
        {
            // Cold cached titles are worth less than the play that needs their space.
            Evict(_ => FreeSpace(dir) is { } now && now < required, reclaim, "free disk space");
        }

        if (FreeSpace(dir) is { } free && free < required)
        {
            throw new InsufficientDiskSpaceException(
                $"Not enough free disk space for the AudioVault cache ({free / (1024 * 1024)} MB free, "
                + $"{(minFreeBytes + needed) / (1024 * 1024)} MB needed). Free up space or lower the cache size.");
        }
    }

    /// <summary>
    /// Deletes least recently used entries until under the limit. Evicted titles cost a new download next time.
    /// Busy entries (being prepared or played) are never evicted, so the cache can stay over the limit while they
    /// are in use, e.g. when a single title is bigger than the whole limit.
    /// </summary>
    public void EnforceLimit(long maxBytes, IReadOnlySet<string> busyEntryIds) =>
        EnforceLimit(maxBytes, (name, delete) => !busyEntryIds.Contains(name) && delete());

    /// <param name="evict">Called with each candidate's folder name and a delete action, least recently used first;
    /// returns whether it deleted. Lets the caller decide and delete atomically with its own bookkeeping.</param>
    public void EnforceLimit(long maxBytes, Func<string, Func<bool>, bool> evict)
    {
        if (maxBytes > 0)
        {
            Evict(total => total > maxBytes, evict, $"its limit of {maxBytes} bytes");
        }
    }

    /// <param name="needMore">Given the remaining total of the plugin's entries, whether to evict another.</param>
    private void Evict(Func<long, bool> needMore, Func<string, Func<bool>, bool> evict, string goal)
    {
        var dir0 = root();
        if (!Directory.Exists(dir0))
        {
            return;
        }

        if (!EnsureRoot())
        {
            logger.LogWarning("Not evicting cached titles: {Problem}", OwnershipProblem);
            return;
        }

        var dirs = OwnedEntryDirs(dir0)
            .Select(d => (Dir: d, Size: SizeOf(d)))
            .OrderBy(d => d.Dir.LastWriteTimeUtc)
            .ToList();
        var total = dirs.Sum(d => d.Size);
        foreach (var (dir, size) in dirs)
        {
            if (!needMore(total))
            {
                break;
            }

            BeforeEvictionCheck?.Invoke(dir.Name);
            var deleted = evict(dir.Name, () =>
            {
                logger.LogInformation("Evicting cached media {Entry} ({Size} bytes)", dir.Name, size);
                try
                {
                    dir.Delete(recursive: true);
                    return true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(ex, "Could not evict {Entry}", dir.Name);
                    return false;
                }
            });
            if (deleted)
            {
                total -= size;
            }
        }

        if (needMore(total))
        {
            logger.LogWarning("Cache could not be brought within {Goal} ({Total} bytes) because the remaining titles are in use", goal, total);
        }
    }

    /// <summary>
    /// Deletes half-written files in the plugin's own entry and placeholder folders. With
    /// <paramref name="olderThanUtc"/>, only files last written before then.
    /// </summary>
    public void CleanupStale(DateTime? olderThanUtc = null)
    {
        var dir = root();
        if (!Directory.Exists(dir))
        {
            return;
        }

        if (!EnsureRoot())
        {
            logger.LogWarning("Not cleaning up the cache: {Problem}", OwnershipProblem);
            return;
        }

        var files = OwnedEntryDirs(dir)
            .SelectMany(d => new[] { "*.live.mp4", "*.remux.mp4", "*.part", "*.tmp.mp4" }.SelectMany(p => SafeEnumerate(d.FullName, p, AllFiles)))
            .Concat(SafeEnumerate(Path.Combine(dir, PlaceholderDirName), "*.tmp.mp4", TopFiles))
            .Concat(SafeEnumerate(dir, "placeholder.mp4.*.tmp.mp4", TopFiles));
        foreach (var file in files)
        {
            if (olderThanUtc is { } cutoff && File.GetLastWriteTimeUtc(file) >= cutoff)
            {
                continue;
            }

            TryDelete(file);
        }
    }

    /// <summary>Test hook: runs before each eviction candidate is offered.</summary>
    internal Action<string>? BeforeEvictionCheck { get; set; }

    public static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static IEnumerable<DirectoryInfo> OwnedEntryDirs(string dir) =>
        new DirectoryInfo(dir).GetDirectories()
            .Where(d => d.Name != PlaceholderDirName && d.LinkTarget is null && File.Exists(Path.Combine(d.FullName, EntryMarkerName)));

    private static IEnumerable<string> SafeEnumerate(string dir, string pattern, EnumerationOptions options)
    {
        try
        {
            return Directory.Exists(dir) ? Directory.EnumerateFiles(dir, pattern, options).ToList() : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static void MarkEntry(string dir)
    {
        var marker = Path.Combine(dir, EntryMarkerName);
        if (!File.Exists(marker))
        {
            File.WriteAllText(marker, string.Empty);
        }
    }

    /// <summary>True when every item in the folder is something an earlier version of the plugin wrote.</summary>
    private static bool IsLegacyCache(string dir)
    {
        var any = false;
        foreach (var item in new DirectoryInfo(dir).EnumerateFileSystemInfos())
        {
            var ok = item switch
            {
                FileInfo f => f.Name == "placeholder.mp4" || f.Name.EndsWith(".tmp.mp4", StringComparison.Ordinal) && f.Name.StartsWith("placeholder.mp4.", StringComparison.Ordinal),
                DirectoryInfo d when d.Name == PlaceholderDirName => d.EnumerateFileSystemInfos().All(i => i is FileInfo && i.Name.EndsWith(".mp4", StringComparison.Ordinal)),
                DirectoryInfo d => IsLegacyEntry(d.FullName) is not false,
                _ => false,
            };
            if (!ok)
            {
                return false;
            }

            any = true;
        }

        return any;
    }

    /// <summary>
    /// Whether a folder holds only files of a cached title (null when it is empty): files named as the plugin names
    /// them, and extraction folders holding nothing but audio files.
    /// </summary>
    private static bool? IsLegacyEntry(string dir)
    {
        if (!SafeIdPattern().IsMatch(Path.GetFileName(dir)))
        {
            return false;
        }

        var items = new DirectoryInfo(dir).EnumerateFileSystemInfos().ToList();
        if (items.Count == 0)
        {
            return null;
        }

        return items.All(i => i switch
        {
            FileInfo f => LegacyEntryFilePattern().IsMatch(f.Name),
            DirectoryInfo d => d.Name is "extracted" or "extracted.tmp" && HoldsOnlyAudio(d),
            _ => false,
        });
    }

    private static bool HoldsOnlyAudio(DirectoryInfo dir) =>
        dir.EnumerateFileSystemInfos("*", EverythingBelow)
            .All(i => i is DirectoryInfo || (i is FileInfo && EpisodeMapper.IsAudio(i.Name)));

    [System.Text.RegularExpressions.GeneratedRegex(@"^[A-Za-z0-9_-]+$")]
    private static partial System.Text.RegularExpressions.Regex SafeIdPattern();

    [System.Text.RegularExpressions.GeneratedRegex(@"^(source\.(audio|archive)(\.part)?|episodes\.json(\.tmp)?|(main|e\d{3,})(\.live|\.remux)?\.mp4|\.audiovault-entry)$")]
    private static partial System.Text.RegularExpressions.Regex LegacyEntryFilePattern();

    private static long SizeOf(DirectoryInfo dir)
    {
        long total = 0;
        try
        {
            foreach (var file in dir.EnumerateFiles("*", AllFiles))
            {
                try
                {
                    total += file.Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Deleted while counting.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return total;
    }

    private static long? DefaultFreeSpace(string dir)
    {
        try
        {
            // On Unix any path works and reports its own mount; Windows wants the drive root.
            var name = OperatingSystem.IsWindows() ? Path.GetPathRoot(Path.GetFullPath(dir))! : Path.GetFullPath(dir);
            return new DriveInfo(name).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static string Key(int? episode) => episode is { } e ? $"e{e:000}" : "main";

    private static string SafeId(string id) =>
        string.Concat(id.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
}
