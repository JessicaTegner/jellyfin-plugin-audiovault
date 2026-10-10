using System.Collections.Concurrent;
using AudioVault.Core;

namespace Jellyfin.Plugin.AudioVault.Services;

/// <summary>Where the plugin may keep its folders, as plain values.</summary>
/// <param name="Defaults">Movies, shows and cache folders used when a field is empty or unusable.</param>
/// <param name="Contains">Folders the plugin's folders must not be, or contain (Jellyfin's data, cache, the plugin's own data, home folders).</param>
/// <param name="Forbidden">Folders the plugin's folders must not be, contain, or be inside (Jellyfin's config, logs, web client, program files).</param>
public sealed record FolderContext(FolderSet Defaults, IReadOnlyList<string> Contains, IReadOnlyList<string> Forbidden);

public sealed record FolderSet(string Movies, string Shows, string Cache);

/// <summary>The folders in use and, if a configured one couldn't be used, why.</summary>
public sealed record FolderChoice(FolderSet Folders, IReadOnlyList<string> Problems);

/// <summary>
/// The plugin deletes files in its folders (stale .strm files, evicted cache entries), so each must be a folder of its
/// own: absolute, not a system or Jellyfin folder, and not overlapping another.
/// </summary>
public static class FolderRules
{
    private static readonly string[] SystemFolders =
    [
        "/bin", "/boot", "/dev", "/etc", "/home", "/lib", "/lib32", "/lib64", "/opt", "/proc", "/root", "/run", "/sbin",
        "/srv", "/sys", "/tmp", "/usr", "/var", "/mnt", "/media", "/data", "/config", "/cache",
        "/Users", "/Volumes", "/Applications", "/Library", "/System", "/private",
    ];

    public static FolderChoice Choose(string? movies, string? shows, string? cache, FolderContext context)
    {
        var problems = new List<string>();
        var fields = new[]
        {
            (Configured: movies, Fallback: context.Defaults.Movies, Label: "movies library folder"),
            (Configured: shows, Fallback: context.Defaults.Shows, Label: "shows library folder"),
            (Configured: cache, Fallback: context.Defaults.Cache, Label: "cache folder"),
        };
        var chosen = new List<string>();
        for (var i = 0; i < fields.Length; i++)
        {
            var (configured, fallback, label) = fields[i];
            var path = fallback;
            if (!string.IsNullOrWhiteSpace(configured))
            {
                // Later fields are checked against this one; their defaults must stay clear of it too.
                var others = chosen.Concat(fields.Skip(i + 1).Select(f => f.Fallback));
                if (Problem(configured, context, others) is { } problem)
                {
                    problems.Add($"The {label} {configured.Trim()} can't be used: it {problem}.");
                }
                else
                {
                    path = Normalize(configured)!;
                }
            }

            chosen.Add(path);
        }

        return new FolderChoice(new FolderSet(chosen[0], chosen[1], chosen[2]), problems);
    }

    /// <summary>Why a configured folder can't be used (completes "it …"), or null.</summary>
    public static string? Problem(string path, FolderContext context, IEnumerable<string> others)
    {
        if (Normalize(path) is not { } full)
        {
            return "is not a full path starting at the root of the file system";
        }

        if (Path.GetPathRoot(full) is { } root && Same(full, root))
        {
            return "is the root of the file system";
        }

        if (SystemFolders.Any(s => Same(full, s)) || context.Contains.Any(c => Same(full, c) || Inside(c, full)))
        {
            return "is or contains a system, home or Jellyfin folder";
        }

        if (context.Forbidden.Any(f => Same(full, f) || Inside(f, full) || Inside(full, f)))
        {
            return "is part of Jellyfin's own program, configuration or log folders";
        }

        if (others.Any(o => Same(full, o) || Inside(o, full) || Inside(full, o)))
        {
            return "is the same as, or overlaps, another AudioVault folder";
        }

        return null;
    }

    /// <summary>Absolute path without a trailing separator, or null if it isn't absolute.</summary>
    public static string? Normalize(string? path)
    {
        var trimmed = path?.Trim();
        if (string.IsNullOrEmpty(trimmed) || !Path.IsPathFullyQualified(trimmed))
        {
            return null;
        }

        try
        {
            var full = Path.GetFullPath(trimmed);
            var root = Path.GetPathRoot(full) ?? string.Empty;
            return full.Length > root.Length ? Path.TrimEndingDirectorySeparator(full) : full;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    // Physical identities are looked up often (every settings read), so they are remembered for a few seconds.
    private static readonly TimeSpan PhysicalTtl = TimeSpan.FromSeconds(10);
    private static readonly ConcurrentDictionary<string, (string Physical, long At)> PhysicalCache = new();

    // Case-insensitive on purpose: on a case-insensitive file system differently cased paths are the same folder.
    // Both as written and as the physical folder (links resolved), so an alias can't make two folders look apart.
    private static bool Same(string a, string b)
    {
        var (la, lb) = (Normalize(a) ?? a, Normalize(b) ?? b);
        return string.Equals(la, lb, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Physical(la), Physical(lb), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether <paramref name="child"/> is strictly inside <paramref name="parent"/>, as written or physically.</summary>
    private static bool Inside(string child, string parent)
    {
        var (c, p) = (Normalize(child) ?? child, Normalize(parent) ?? parent);
        return IsInside(c, p) || IsInside(Physical(c), Physical(p));
    }

    private static bool IsInside(string c, string p)
    {
        var prefix = p.EndsWith(Path.DirectorySeparatorChar) ? p : p + Path.DirectorySeparatorChar;
        return c.Length > prefix.Length && c.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The folder with symlinks and junctions resolved; as written if that fails.</summary>
    private static string Physical(string path)
    {
        var now = Environment.TickCount64;
        if (PhysicalCache.TryGetValue(path, out var known) && now - known.At < PhysicalTtl.TotalMilliseconds)
        {
            return known.Physical;
        }

        string physical;
        try
        {
            physical = PathIdentity.Physical(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            physical = path;
        }

        if (PhysicalCache.Count > 1000)
        {
            PhysicalCache.Clear();
        }

        PhysicalCache[path] = (physical, now);
        return physical;
    }
}
