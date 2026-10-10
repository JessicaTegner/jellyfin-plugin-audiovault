namespace Jellyfin.Plugin.AudioVault.Services;

/// <summary>
/// When an item may get a runtime estimate from metadata. Only empty runtimes and the ~2 s that a probe of our
/// placeholder records are replaced; anything longer came from probing real media (or is an earlier estimate).
/// </summary>
public static class RuntimeRules
{
    public static readonly TimeSpan PlaceholderLimit = TimeSpan.FromSeconds(3);

    public static bool HasRealRuntime(long? ticks) => ticks > PlaceholderLimit.Ticks;

    /// <returns>The ticks to store, or null to leave the item alone.</returns>
    public static long? ToApply(long? currentTicks, TimeSpan? estimate) =>
        estimate is { } e && e > PlaceholderLimit && !HasRealRuntime(currentTicks) && currentTicks != e.Ticks ? e.Ticks : null;

    public static TimeSpan? FromMinutes(int? minutes) => minutes > 0 ? TimeSpan.FromMinutes(minutes.Value) : null;

    /// <summary>The length a probe placeholder should have: the item's stored runtime if real, else the estimate.</summary>
    public static TimeSpan? ProbeLength(long? itemTicks, TimeSpan? estimate) =>
        HasRealRuntime(itemTicks) ? TimeSpan.FromTicks(itemTicks!.Value) : estimate;
}
