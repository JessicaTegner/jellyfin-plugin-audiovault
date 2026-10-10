using System.Diagnostics;

namespace AudioVault.Sources.AudioVault.Tests;

public class RequestThrottleTests
{
    [Fact]
    public void GapsStayWithinTheRangeAndVary()
    {
        var gaps = Enumerable.Range(0, 1000)
            .Select(_ => RequestThrottle.NextGap(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)))
            .ToList();

        Assert.All(gaps, g => Assert.InRange(g.TotalSeconds, 1, 2));
        Assert.True(gaps.Distinct().Count() > 900);
        Assert.InRange(gaps.Average(g => g.TotalSeconds), 1.4, 1.6);
    }

    [Fact]
    public async Task SpacesConsecutiveRequests()
    {
        using var throttle = new RequestThrottle();
        var gap = TimeSpan.FromMilliseconds(100);
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 4; i++)
        {
            await throttle.WaitAsync(gap, gap, default);
        }

        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(290), watch.Elapsed.ToString());
    }
}
