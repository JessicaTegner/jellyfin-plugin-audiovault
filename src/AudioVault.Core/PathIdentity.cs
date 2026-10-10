namespace AudioVault.Core;

/// <summary>
/// Compares folders by the physical directory they name: symlinks and junctions in the existing part of a path are
/// resolved, so two differently spelled paths to one folder count as the same folder.
/// </summary>
public static class PathIdentity
{
    private const int MaxLinkHops = 40;

    /// <summary>Case-insensitive on Windows and macOS (their usual file systems), ordinal elsewhere.</summary>
    public static StringComparison Comparison { get; } =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static StringComparer Comparer { get; } =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>
    /// The absolute path with every link among its existing ancestors (and itself) resolved, without a trailing
    /// separator. A suffix that doesn't exist yet is kept as written.
    /// </summary>
    public static string Physical(string path)
    {
        var hops = 0;
        return Trim(Resolve(Path.GetFullPath(path), ref hops));
    }

    public static bool Same(string a, string b) => string.Equals(Physical(a), Physical(b), Comparison);

    /// <summary>Whether <paramref name="child"/> is <paramref name="parent"/> or somewhere beneath it.</summary>
    public static bool SameOrInside(string child, string parent) => IsSameOrInside(Physical(child), Physical(parent));

    /// <summary>Whether either folder is the other or contains it.</summary>
    public static bool Overlaps(string a, string b)
    {
        var pa = Physical(a);
        var pb = Physical(b);
        return IsSameOrInside(pa, pb) || IsSameOrInside(pb, pa);
    }

    /// <summary>For paths already made <see cref="Physical"/>: whether the child lies beneath the parent (not equal).</summary>
    internal static bool IsBeneath(string physicalChild, string physicalParent) =>
        !string.Equals(physicalChild, physicalParent, Comparison) && IsSameOrInside(physicalChild, physicalParent);

    private static bool IsSameOrInside(string child, string parent)
    {
        if (string.Equals(child, parent, Comparison))
        {
            return true;
        }

        var prefix = Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar;
        return child.StartsWith(prefix, Comparison);
    }

    private static string Resolve(string full, ref int hops)
    {
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root))
        {
            return full;
        }

        var parts = full[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (var i = 0; i < parts.Length; i++)
        {
            var next = Path.Combine(current, parts[i]);
            FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
            if (!info.Exists)
            {
                // Nothing further exists, so nothing further can be a link: keep the rest as written.
                return Path.Combine([current, .. parts[i..]]);
            }

            FileSystemInfo? target = null;
            try
            {
                target = info.LinkTarget is null ? null : info.ResolveLinkTarget(returnFinalTarget: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A link loop or an unreadable link: compare it as written.
            }

            if (target is not null && ++hops <= MaxLinkHops)
            {
                // The target's own ancestors may be links too (e.g. /tmp -> /private/tmp on macOS).
                current = Resolve(Path.GetFullPath(target.FullName), ref hops);
            }
            else
            {
                current = next;
            }
        }

        return current;
    }

    private static string Trim(string path)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(path);
        return trimmed.Length == 0 ? path : trimmed;
    }
}
