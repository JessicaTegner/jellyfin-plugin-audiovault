using Jellyfin.Plugin.AudioVault.Services;

namespace Jellyfin.Plugin.AudioVault.Tests;

public sealed class RuntimeRulesTests
{
    private static readonly TimeSpan Estimate = TimeSpan.FromMinutes(97);

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(20_000_000L)] // the 2 s placeholder
    [InlineData(30_000_000L)]
    public void FillsEmptyAndPlaceholderRuntimes(long? current) =>
        Assert.Equal(Estimate.Ticks, RuntimeRules.ToApply(current, Estimate));

    [Theory]
    [InlineData(30_000_001L)]
    [InlineData(36_000_000_000L)] // a real probe: one hour
    public void LeavesRealRuntimesAlone(long current) =>
        Assert.Null(RuntimeRules.ToApply(current, Estimate));

    [Fact]
    public void LeavesAnEarlierEstimateAlone() =>
        Assert.Null(RuntimeRules.ToApply(Estimate.Ticks, TimeSpan.FromMinutes(98)));

    [Fact]
    public void NothingToApplyWithoutAUsableEstimate()
    {
        Assert.Null(RuntimeRules.ToApply(null, null));
        Assert.Null(RuntimeRules.ToApply(null, TimeSpan.FromSeconds(2)));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(0, null)]
    [InlineData(-5, null)]
    [InlineData(42, 42)]
    public void TmdbMinutes(int? minutes, int? expected) =>
        Assert.Equal(expected is { } m ? TimeSpan.FromMinutes(m) : null, RuntimeRules.FromMinutes(minutes));

    [Fact]
    public void ProbeLengthPrefersTheStoredRealRuntime()
    {
        var real = TimeSpan.FromMinutes(101);
        Assert.Equal(real, RuntimeRules.ProbeLength(real.Ticks, Estimate));
        Assert.Equal(Estimate, RuntimeRules.ProbeLength(20_000_000L, Estimate));
        Assert.Equal(Estimate, RuntimeRules.ProbeLength(null, Estimate));
        Assert.Null(RuntimeRules.ProbeLength(null, null));
    }
}
