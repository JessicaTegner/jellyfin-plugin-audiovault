using AudioVault.Core.Media;

namespace AudioVault.Core.Tests;

public class JobProgressTrackerTests
{
    [Fact]
    public void Download_hides_the_conversion_it_feeds_and_reports_percent()
    {
        var tracker = new JobProgressTracker();
        using var conversion = tracker.Start("1", null, "Film", JobPhase.Converting);
        using var other = tracker.Start("2", 3, "Show S01E03", JobPhase.Converting);
        var download = tracker.Start("1", null, "Film", JobPhase.Waiting);

        Assert.Equal([JobPhase.Converting, JobPhase.Waiting], tracker.Active().Select(p => p.Phase));

        download.Phase = JobPhase.Downloading;
        download.SetTotal(400);
        download.AddBytes(100);
        var film = tracker.Active().Single(p => p.EntryId == "1");
        Assert.Equal((100L, (long?)400, (double?)25), (film.BytesReceived, film.TotalBytes, film.Percent));

        download.Dispose();
        Assert.Equal(JobPhase.Converting, tracker.Active().Single(p => p.EntryId == "1").Phase);
    }

    [Fact]
    public void Unknown_length_has_no_percent()
    {
        var tracker = new JobProgressTracker();
        using var download = tracker.Start("1", null, "Film", JobPhase.Downloading);
        download.SetTotal(null);
        download.AddBytes(10);

        var progress = Assert.Single(tracker.Active());
        Assert.Null(progress.TotalBytes);
        Assert.Null(progress.Percent);
    }

    [Fact]
    public void Keeps_the_last_twenty_outcomes_with_increasing_sequence()
    {
        var tracker = new JobProgressTracker();
        for (var i = 0; i < 25; i++)
        {
            tracker.Record(i.ToString(), null, "T" + i, JobOutcomeKind.MovieReady);
        }

        var outcomes = tracker.RecentOutcomes();
        Assert.Equal(20, outcomes.Count);
        Assert.Equal(6, outcomes[0].Sequence);
        Assert.Equal(25, outcomes[^1].Sequence);
    }

    [Fact]
    public void Jobs_with_the_same_title_have_distinct_stable_ids()
    {
        var tracker = new JobProgressTracker();
        using var a = tracker.Start("1", null, "Film", JobPhase.Downloading);
        using var b = tracker.Start("2", null, "Film", JobPhase.Downloading);

        var first = tracker.Active().Select(p => p.Id).ToList();
        a.Phase = JobPhase.Converting;
        b.AddBytes(10);

        Assert.Equal(2, first.Distinct().Count());
        Assert.Equal(first, tracker.Active().Select(p => p.Id));
    }
}
