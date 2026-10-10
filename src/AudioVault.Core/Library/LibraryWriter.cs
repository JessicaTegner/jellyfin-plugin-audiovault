using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace AudioVault.Core.Library;

/// <param name="Failed">Files that could not be written or deleted; a later sync retries them.</param>
/// <param name="Foreign">.strm files under the root the plugin doesn't own; never touched.</param>
/// <param name="Skipped">Files kept by the safety rail although they are no longer wanted.</param>
/// <param name="Deferred">Old files kept because the file replacing them couldn't be written; counted in Failed.</param>
public sealed record WriteResult(int Written, int Deleted, bool DeletionsSkipped, int Failed = 0, int Foreign = 0, int Skipped = 0, int Deferred = 0);

/// <summary>
/// Which item each file is for, so a file that moves to a new path is only removed once its replacement is written.
/// </summary>
/// <param name="OfDesired">The key of each wanted path.</param>
/// <param name="OfContent">The key of an existing file, from its content, or null if unknown.</param>
/// <param name="IsDeliberate">
/// For the key of a file that is no longer wanted at all: whether its removal is deliberate (a settings change), so
/// it doesn't count against the safety rail.
/// </param>
public sealed record LinkKeys(
    IReadOnlyDictionary<string, string> OfDesired,
    Func<string, string?> OfContent,
    Func<string, bool> IsDeliberate);

/// <summary>
/// Mirrors a desired set of .strm files onto a folder. Only files listed in the folder's inventory (files this writer
/// created) are ever replaced or deleted, so pointing it at a folder with other content can't destroy that content.
/// A file moving to a new path is deleted only after its replacement is written. Deleting many other files at once,
/// more than looks like normal catalogue churn, is skipped, unless the caller marks each removal as deliberate.
/// </summary>
public sealed class LibraryWriter(ILogger<LibraryWriter> logger)
{
    public const double MaxDeleteFraction = 0.2;

    public const string InventoryFileName = ".audiovault-library.json";

    private readonly ConcurrentDictionary<string, StringComparer> _comparers = new();

    /// <param name="isLegacyOwned">
    /// For a folder without an inventory (written by an older version): whether an existing .strm file's content shows
    /// it is ours. Files it rejects are left alone.
    /// </param>
    public WriteResult Sync(
        string root,
        IReadOnlyDictionary<string, string> desired,
        Func<string, bool>? isLegacyOwned = null,
        LinkKeys? keys = null)
    {
        Directory.CreateDirectory(root);
        var comparer = _comparers.GetOrAdd(PathIdentity.Physical(root), r => IsCaseInsensitive(r) ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var inventoryPath = Path.Combine(root, InventoryFileName);
        var existing = Directory.EnumerateFiles(root, "*.strm", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(root, p))
            .ToHashSet(comparer);

        var wanted = desired.Keys.ToHashSet(comparer);
        var inventory = JsonFile.Read<List<string?>>(inventoryPath);
        var owned = inventory is not null
            ? inventory.OfType<string>().ToHashSet(comparer)
            : existing.Where(p => isLegacyOwned is not null && IsLegacyOwned(Path.Combine(root, p), isLegacyOwned)).ToHashSet(comparer);
        var foreign = existing.Where(p => !owned.Contains(p)).ToList();
        if (foreign.Count > 0)
        {
            logger.LogWarning(
                "{Count} .strm files under {Root} were not created by AudioVault; they are left untouched",
                foreign.Count,
                root);
        }

        // Record ownership before touching anything, so files written by an interrupted run are still ours next time.
        var desiredOwned = desired.Keys.Where(p => !existing.Contains(p) || owned.Contains(p)).ToList();
        owned.UnionWith(desiredOwned);
        JsonFile.Write(inventoryPath, owned.Order(StringComparer.Ordinal).ToList());

        int written = 0, failed = 0;
        var persisted = new HashSet<string>(comparer);
        foreach (var relative in desiredOwned)
        {
            var content = desired[relative];
            var full = Path.Combine(root, relative);
            try
            {
                if (!(existing.Contains(relative) && File.ReadAllText(full) == content))
                {
                    WriteAtomically(full, content);
                    written++;
                }

                persisted.Add(relative);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed++;
                logger.LogWarning(ex, "Could not write {Path}", full);
            }
        }

        var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (path, key) in keys?.OfDesired ?? new Dictionary<string, string>())
        {
            if (desired.ContainsKey(path))
            {
                replacements.TryAdd(key, path);
            }
        }

        var orphans = existing.Where(p => owned.Contains(p) && !wanted.Contains(p)).ToList();
        var removable = new List<string>();
        var unexplained = new HashSet<string>(comparer);
        var deferred = 0;
        foreach (var relative in orphans)
        {
            var key = keys is null ? null : KeyOf(Path.Combine(root, relative), keys.OfContent);
            if (key is not null && replacements.TryGetValue(key, out var replacement))
            {
                if (persisted.Contains(replacement))
                {
                    removable.Add(relative);
                    continue;
                }

                // The old file stays until the next sync manages to write its replacement.
                deferred++;
                if (!owned.Contains(replacement))
                {
                    failed++;
                    logger.LogWarning(
                        "Could not write {Path}: a file AudioVault didn't create is in the way; keeping {Old}",
                        Path.Combine(root, replacement),
                        Path.Combine(root, relative));
                }

                continue;
            }

            if (key is null || !keys!.IsDeliberate(key))
            {
                unexplained.Add(relative);
            }

            removable.Add(relative);
        }

        var skipped = unexplained.Count > 50 && unexplained.Count > owned.Count * MaxDeleteFraction;
        var deleted = 0;
        if (skipped)
        {
            logger.LogWarning(
                "Would delete {Count} of {Total} .strm files under {Root} that the catalogue no longer lists for the library; skipping those deletions as a safety measure",
                unexplained.Count,
                owned.Count,
                root);
        }

        foreach (var relative in skipped ? removable.Where(p => !unexplained.Contains(p)) : removable)
        {
            var full = Path.Combine(root, relative);
            try
            {
                File.Delete(full);
                owned.Remove(relative);
                deleted++;
                RemoveEmptyParents(root, Path.GetDirectoryName(full)!);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed++;
                logger.LogWarning(ex, "Could not delete {Path}", full);
            }
        }

        // Owned paths that vanished on their own are no longer ours to track.
        owned.RemoveWhere(p => !wanted.Contains(p) && !existing.Contains(p));
        JsonFile.Write(inventoryPath, owned.Order(StringComparer.Ordinal).ToList());
        return new WriteResult(written, deleted, skipped, failed, foreign.Count, skipped ? unexplained.Count : 0, deferred);
    }

    private static string? KeyOf(string path, Func<string, string?> ofContent)
    {
        try
        {
            return new FileInfo(path).Length < 4096 ? ofContent(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsLegacyOwned(string path, Func<string, bool> isLegacyOwned)
    {
        try
        {
            return new FileInfo(path).Length < 4096 && isLegacyOwned(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // Write then rename, so a failure never leaves a truncated link behind.
    private static void WriteAtomically(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(tmp, content);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            File.Delete(tmp);
            throw;
        }
    }

    private static void RemoveEmptyParents(string root, string dir)
    {
        var top = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        for (var current = Path.GetFullPath(dir);
             current.Length > top.Length && current.StartsWith(top, StringComparison.Ordinal);
             current = Path.GetDirectoryName(current)!)
        {
            if (Directory.EnumerateFileSystemEntries(current).Any())
            {
                return;
            }

            Directory.Delete(current);
        }
    }

    private static bool IsCaseInsensitive(string root)
    {
        var probe = Path.Combine(root, $".avcase-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(probe, string.Empty);
            return File.Exists(probe.ToUpperInvariant());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
        }
        finally
        {
            try
            {
                File.Delete(probe);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
