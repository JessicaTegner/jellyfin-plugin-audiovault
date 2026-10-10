using Jellyfin.Plugin.AudioVault.Tasks;

namespace Jellyfin.Plugin.AudioVault.Tests;

public class ScheduleJitterTests
{
    [Fact]
    public void WeeklySlotIsStablePerServerAndInsideTheWindow()
    {
        var a = ScheduleJitter.WeeklySlot("server-a");
        Assert.Equal(a, ScheduleJitter.WeeklySlot("server-a"));
        Assert.InRange(a.Time, TimeSpan.FromHours(3), TimeSpan.FromHours(6));

        var slots = Enumerable.Range(0, 200).Select(i => ScheduleJitter.WeeklySlot("server-" + i)).ToList();
        Assert.All(slots, s => Assert.InRange(s.Time, TimeSpan.FromHours(3), TimeSpan.FromHours(6)));
        Assert.True(slots.Select(s => s.Day).Distinct().Count() == 7);
        Assert.True(slots.Select(s => s.Time).Distinct().Count() > 100);
    }

    [Fact]
    public void RecognisesScheduledStarts()
    {
        var slot = new[] { ((DayOfWeek?)DayOfWeek.Tuesday, TimeSpan.FromHours(4.5)) };
        var tuesday = new DateTime(2026, 10, 13, 4, 31, 0);

        Assert.True(ScheduleJitter.IsScheduledStart(tuesday, slot));
        Assert.False(ScheduleJitter.IsScheduledStart(tuesday.AddMinutes(10), slot));
        Assert.False(ScheduleJitter.IsScheduledStart(tuesday.AddDays(1), slot));
        Assert.True(ScheduleJitter.IsScheduledStart(tuesday.AddDays(1), [(null, TimeSpan.FromHours(4.5))]));
    }

    [Fact]
    public void RandomDelayIsAtMostAnHour() =>
        Assert.All(Enumerable.Range(0, 500).Select(_ => ScheduleJitter.RandomDelay()), d => Assert.InRange(d, TimeSpan.Zero, TimeSpan.FromHours(1)));
}
