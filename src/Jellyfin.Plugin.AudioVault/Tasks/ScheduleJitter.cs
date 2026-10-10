using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.AudioVault.Tasks;

/// <summary>Keeps scheduled visits to the source from happening at the same moment every week, or on every server.</summary>
public static class ScheduleJitter
{
    public static readonly TimeSpan EarliestStart = TimeSpan.FromHours(3);
    public static readonly TimeSpan StartWindow = TimeSpan.FromHours(3);
    public static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(60);

    /// <summary>A weekday and time between 03:00 and 06:00 that stays the same for one server but differs between servers.</summary>
    public static (DayOfWeek Day, TimeSpan Time) WeeklySlot(string serverSeed)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("audiovault-weekly|" + serverSeed));
        var value = BitConverter.ToUInt32(hash, 0);
        var day = (DayOfWeek)(value % 7);
        var minutes = (int)(value / 7 % (uint)StartWindow.TotalMinutes);
        return (day, EarliestStart + TimeSpan.FromMinutes(minutes));
    }

    /// <summary>True when "now" is right at one of the task's scheduled times, i.e. the scheduler started it rather than a person.</summary>
    public static bool IsScheduledStart(DateTime localNow, IEnumerable<(DayOfWeek? Day, TimeSpan Time)> slots, TimeSpan? tolerance = null)
    {
        var slack = tolerance ?? TimeSpan.FromMinutes(2);
        return slots.Any(s =>
            (s.Day is null || s.Day == localNow.DayOfWeek)
            && (localNow.TimeOfDay - s.Time).Duration() <= slack);
    }

    public static TimeSpan RandomDelay() => TimeSpan.FromSeconds(Random.Shared.Next(0, (int)MaxDelay.TotalSeconds + 1));
}
