using AudioVault.Core.Media;
using AudioVault.Core.Sources;
using Jellyfin.Plugin.AudioVault.Services;

namespace Jellyfin.Plugin.AudioVault.Tests;

public sealed class DiagnosticReportTests
{
    private const string Email = "someone+av@mailbox.example.org";
    private const string Token = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";
    private const string PublicUrl = "https://jelly.my-home.example.com:8920";

    private static readonly Redactor Redact = new(
    [
        new(PublicUrl, "server address"),
        new("http://flaresolverr.lan:8191", "FlareSolverr address"),
        new("/config/data/audiovault/library/movies", "movies folder", true),
        new("/cache/audiovault", "cache folder", true),
        new("sam", "user name"),
        new(Email, "AudioVault login"),
        new("Living room TV", "device name"),
    ],
    ["Bob's Burgers", "The Matrix (1999)", "The Lord of the Rings", "Up"]);

    [Theory]
    [InlineData("Signed in as someone.else@gmail.com.", "someone.else@gmail.com")]
    [InlineData("Signed in as " + Email + ".", Email)]
    [InlineData("Login SOMEONE+AV@MAILBOX.EXAMPLE.ORG failed", "SOMEONE+AV")]
    public void EmailsNeverLeak(string text, string secret)
    {
        var scrubbed = Redact.Scrub(text);
        Assert.DoesNotContain(secret, scrubbed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@", scrubbed);
    }

    [Theory]
    [InlineData("Request failed: Authorization: MediaBrowser Token=\"" + Token + "\"")]
    [InlineData("GET /AudioVault/Stream/123?sig=" + Token + "&avt=abc")]
    [InlineData("api_key=" + Token)]
    [InlineData("remember_web_59ba36addc2b2f9401580f014c7f58ea4e30989d=" + Token)]
    [InlineData("Bearer " + Token)]
    [InlineData("cookie: " + Token)]
    [InlineData("stray value " + Token + " in a message")]
    [InlineData("password=hunter2-correct-horse")]
    public void TokensNeverLeak(string text)
    {
        var scrubbed = Redact.Scrub(text);
        Assert.DoesNotContain(Token, scrubbed);
        Assert.DoesNotContain("hunter2", scrubbed);
        Assert.DoesNotContain("59ba36addc2b2f9401580f014c7f58ea4e30989d", scrubbed);
    }

    [Theory]
    [InlineData("This server can't use its own public URL " + PublicUrl + ": timed out", "my-home")]
    [InlineData("Could not reach https://other.example.net/web/index.html", "other.example.net")]
    [InlineData("FlareSolverr at http://flaresolverr.lan:8191/v1 failed", "flaresolverr.lan")]
    [InlineData("A player couldn't reach http://192.168.1.20:8096", "192.168.1.20")]
    [InlineData("A player couldn't reach http://[fd7a:115c:a1e0::1]:8096", "fd7a")]
    [InlineData("Resolved jelly.my-home.example.com to 100.101.102.103", "100.101.102.103")]
    [InlineData("Listening on fe80::1c2d:3e4f:5a6b:7c8d", "fe80")]
    [InlineData("Try my-mac.tail1234.ts.net instead", "tail1234")]
    public void UrlsHostsAndIpsNeverLeak(string text, string secret)
    {
        var scrubbed = Redact.Scrub(text);
        Assert.DoesNotContain(secret, scrubbed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("://", scrubbed);
    }

    [Fact]
    public void UrlsKeepOnlySchemeAndKind()
    {
        Assert.Equal("Could not reach [https, public name URL].", Redact.Scrub("Could not reach https://other.example.net/x."));
        Assert.Equal("[http, Tailscale IP URL] failed", Redact.Scrub("http://100.64.1.2:8096/web failed"));
        Assert.Equal("[http, private network IP URL]", Redact.Scrub("http://10.0.0.5:8096"));
        Assert.Equal("[http, localhost URL]", Redact.Scrub("http://localhost:8096"));
        Assert.Equal("[https, audiovault.net URL]", Redact.Scrub("https://audiovault.net/movies?page=1"));
    }

    [Theory]
    [InlineData("http://127.0.0.1:8096", "http, localhost IP")]
    [InlineData("http://localhost", "http, localhost")]
    [InlineData("http://192.168.0.10:8096", "http, private network IP")]
    [InlineData("http://jellyfin.local:8096", "http, private network name")]
    [InlineData("http://my-mac-mini:8096", "http, private network name")]
    [InlineData("http://100.100.1.1:8096", "http, Tailscale IP")]
    [InlineData("https://box.tail9.ts.net", "https, Tailscale name")]
    [InlineData("https://jf.example.com", "https, public name")]
    [InlineData("http://8.8.8.8", "http, public IP")]
    [InlineData("", "none")]
    [InlineData("not a url", "not a valid address")]
    public void DescribesAddressesWithoutNamingThem(string url, string expected)
    {
        Assert.Equal(expected, Redactor.DescribeUrl(url));
    }

    [Theory]
    [InlineData("Access to the path '/config/data/audiovault/library/movies/The Matrix (1999)/The Matrix (1999).strm' is denied.")]
    [InlineData("Could not write /config/data/audiovault/library/movies/Some Film (2020)/Some Film (2020) - UK.strm: disk full")]
    [InlineData("Could not open /srv/media/Other Film/part1.mp3")]
    [InlineData(@"Could not open C:\Users\sam\AppData\Local\jellyfin\cache\Other Film.mp4")]
    [InlineData("Cache folder /cache/audiovault/abc/Some Film.mp4 is full.")]
    public void PathsWithUserContentNeverLeak(string text)
    {
        var scrubbed = Redact.Scrub(text);
        Assert.DoesNotContain("Matrix", scrubbed);
        Assert.DoesNotContain("Film", scrubbed);
        Assert.DoesNotContain("Users", scrubbed);
        Assert.DoesNotContain("/config", scrubbed);
        Assert.DoesNotContain("/cache", scrubbed);
    }

    [Fact]
    public void KnownNamesAndTitlesAreReplacedAsWholeWords()
    {
        Assert.Equal(
            "A player on [device name] (Jellyfin Android TV, [user name]) couldn't load the playback address; the same happened to [title].",
            Redact.Scrub("A player on Living room TV (Jellyfin Android TV, sam) couldn't load the playback address; the same happened to Bob's Burgers."));
    }

    [Fact]
    public void QuotedNamesGoButThePluginsOwnWordingStays()
    {
        Assert.Equal("Downloading \"[name]\" failed", Redact.Scrub("Downloading \"Some Unknown Title\" failed"));
        Assert.Equal("Use \"Run full catalog sync now\".", Redact.Scrub("Use \"Run full catalog sync now\"."));
        Assert.Equal("Jellyfin's TMDb client can't be used; it isn't there.", Redact.Scrub("Jellyfin's TMDb client can't be used; it isn't there."));
    }

    [Fact]
    public void PlainMessagesAreLeftReadable()
    {
        const string text = "Users can still open the account page at /AudioVault/Account on this server. Last read 2026-10-10 12:33 UTC.";
        Assert.Equal(text, Redact.Scrub(text));
    }

    [Theory]
    [InlineData("/cache/audiovault/123/extracted/The Lord of the Rings episode01.mp3", "Lord", "Rings", "episode01")]
    [InlineData("Could not read /srv/media/123/extracted/the hidden garden episode01.mp3 after retrying", "hidden", "garden", "episode01")]
    [InlineData("Archive entry ../my_private_show/episode_one.mp3 points outside its folder", "my_private_show", "episode_one", "../")]
    [InlineData("Archive entry My Private Show/Episode One.mp3 points outside its folder", "Private", "Show", "Episode")]
    [InlineData(@"Archive entry ..\my private show\episode one.mp3 is outside", "private", "show", "episode")]
    [InlineData("Could not reach media.example.online:443 in time", "media", "example", "online")]
    [InlineData("Could not reach backup.my-family.photos or 10.1.2.3:8096", "my-family", "photos", "10.1.2.3")]
    public void PathsAndHostsLeaveNothingBehind(string text, string a, string b, string c)
    {
        var scrubbed = Redact.Scrub(text);
        Assert.DoesNotContain(a, scrubbed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(b, scrubbed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(c, scrubbed, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReviewExamplesAreReplacedWhole()
    {
        Assert.Equal("[cache folder]", Redact.Scrub("/cache/audiovault/123/extracted/The Lord of the Rings episode01.mp3"));
        Assert.Equal("Archive entry [path]", Redact.Scrub("Archive entry ../my_private_show/episode_one.mp3 points outside its folder"));
        Assert.Equal("[host]", Redact.Scrub("media.example.online:443"));
    }

    [Fact]
    public void KnownTitlesGoWholeBeforeAnythingCanSplitThem()
    {
        Assert.Equal("Converting [title] failed: no space left.", Redact.Scrub("Converting The Lord of the Rings failed: no space left."));
        Assert.Equal("Waiting for [title] season 2", Redact.Scrub("Waiting for the lord of the rings season 2"));

        // Short titles only in their own case, so ordinary words stay.
        Assert.Equal("Turn it [title], then up again", Redact.Scrub("Turn it Up, then up again"));
    }

    [Fact]
    public void ManyCatalogTitlesAreMatchedQuickly()
    {
        var titles = Enumerable.Range(0, 30_000).Select(i => $"Catalog Title Number {i}").Append("A Quiet Place").ToList();
        var redactor = new Redactor([], titles);
        var text = string.Join(' ', Enumerable.Repeat("Converting A Quiet Place failed after some time.", 200));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var scrubbed = redactor.Scrub(text);
        Assert.DoesNotContain("Quiet", scrubbed);
        Assert.Contains("Converting [title] failed", scrubbed);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), watch.Elapsed.ToString());
    }

    [Fact]
    public void FailuresReachTheReportAsCategoriesNotText()
    {
        const string raw = "Archive entry ../my_private_show/episode_one.mp3 points outside its folder at media.example.online:443";
        var failures = new[]
        {
            FailureInfo.From(new SourceException(raw) { Kind = FailureKind.ArchiveEntryOutsideFolder }),
            FailureInfo.From(new SourceException(raw, new HttpRequestException(raw, null, System.Net.HttpStatusCode.BadGateway))),
            FailureInfo.From(new ConversionFailedException("ffmpeg exited with 1: " + raw)),
            FailureInfo.From(new IOException(raw, 28)),
            FailureInfo.From(new UnauthorizedAccessException(raw)),
            FailureInfo.From(new InvalidOperationException(raw)),
        };
        var facts = SampleFacts() with
        {
            SourceOk = false,
            SourceMessage = raw,
            SourceFailure = FailureInfo.From(new SourceFormatChangedException(raw)),
            RecentProblems = [.. failures.Select(f => new DiagnosticProblem(DateTimeOffset.UnixEpoch, "Download or conversion failed", f))],
        };

        var report = DiagnosticReport.Build(facts, new Redactor([]));

        Assert.Contains("- AudioVault connection: problem: AudioVault page layout changed (SourceFormatChangedException)", report);
        Assert.Contains("failed: archive entry outside its folder (SourceException)", report);
        Assert.Contains("failed: network error (SourceException, HTTP 502)", report);
        Assert.Contains("failed: conversion failed (ConversionFailedException)", report);
        Assert.Contains("failed: disk full (IOException)", report);
        Assert.Contains("failed: file access denied (UnauthorizedAccessException)", report);
        Assert.Contains("failed: other error (InvalidOperationException)", report);
        foreach (var leak in new[] { "my_private_show", "episode_one", "media.example", "ffmpeg", "points outside" })
        {
            Assert.DoesNotContain(leak, report, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ReportHasEverySectionAndNothingPersonal()
    {
        var report = DiagnosticReport.Build(SampleFacts(), Redact);

        foreach (var heading in new[] { "Versions", "Setup checklist", "Compatibility", "Health", "Catalog and libraries", "Downloads", "Settings", "Tasks", "Recent problems" })
        {
            Assert.Contains("\n" + heading + "\n", report);
        }

        Assert.Contains("- Plugin: 0.1.0.0", report);
        Assert.Contains("- Jellyfin: 12.2.0", report);
        Assert.Contains("- FlareSolverr configured: yes", report);
        Assert.Contains("- Playback address: https, public name (set in this plugin)", report);
        Assert.Contains("Download or conversion failed: AudioVault unavailable or refused (SourceUnavailableException, HTTP 403)", report);
        Assert.Contains("Download or conversion failed: conversion failed (ConversionFailedException)", report);
        Assert.Contains("- Scan Media Library: running, 41%; last failed 2026-10-10 12:33 UTC (the error is in the Jellyfin log)", report);
        Assert.Contains("Fast episode lists aren't available on this Jellyfin version (12.2.0)", report);
        Assert.Contains("- Last 24 hours, all accounts: 10; limit per account: 20", report);
        Assert.Contains("- Your AudioVault login: done. The viewing administrator has a login. 2 of 3 users have one.", report);

        foreach (var secret in new[] { Email, "my-home", "8920", "flaresolverr.lan", "sam", "Living room", "Matrix", "Burgers", "/config", "/cache", Token, "192.168", "@", "part1", "Invalid data" })
        {
            Assert.DoesNotContain(secret, report, StringComparison.OrdinalIgnoreCase);
        }
    }

    internal static DiagnosticFacts SampleFacts()
    {
        var at = new DateTimeOffset(2026, 10, 10, 12, 33, 0, TimeSpan.Zero);
        return new DiagnosticFacts(
            at,
            new DiagnosticServer("0.1.0.0", "12.2.0", "Linux 6.10.14-linuxkit #1 SMP", "X64", ".NET 10.0.0", "Docker"),
            [
                new SetupStep("address", "Playback address works", SetupState.Done, $"Playback links use {PublicUrl}, and this browser reached it."),
                new SetupStep("libraries", "Libraries created", SetupState.Done, "Jellyfin has libraries for the movies and shows folders."),
                new SetupStep("catalog", "Catalog synced", SetupState.Done, "26,174 titles (14,298 movies, 11,876 show seasons), last read {at}.", At: at),
                new SetupStep("matching", "Jellyfin matching titles", SetupState.InProgress, "12,140 of 12,505 movies and 98,789 of 99,403 episodes added."),
                new SetupStep("episodes", "Episode lists", SetupState.Waiting, "9,370 of 10,947 seasons have their episode list; 1,402 wait for Jellyfin to identify their show."),
                new SetupStep("account", "Your AudioVault login", SetupState.Done, $"Signed in as {Email}. 2 of 3 Jellyfin users have one."),
                new SetupStep("compatibility", "Jellyfin compatibility", SetupState.Done, "Jellyfin 12.2.0 works, with limits."),
            ],
            [new CompatibilityIssue("tmdb-seasons", "Fast episode lists aren't available on this Jellyfin version (12.2.0); using the slower per-episode lookup.", false)],
            null,
            [$"A player on Living room TV (Jellyfin Android TV, sam) couldn't load the playback address (last at {{at}}). Check that it can reach {PublicUrl}."],
            true,
            "OK: catalog readable, 573 movie pages",
            null,
            new DiagnosticCounts(26174, 14298, 11876, true, 12505, 99403, 12140, 98789, 12140, 18408, new EpisodeListStatus(10947, 9370, 1577, 1402, false, 175)),
            new DiagnosticDownloads(10, 20, 1, null, 1695203566, 0),
            new DiagnosticSettings(PublicUrl, "set in this plugin", true, "English", true, true, 96, true, true, false, 2, 3),
            1,
            [
                new DiagnosticTask("Full catalog sync", "Idle", null, "Completed", at, false),
                new DiagnosticTask("Scan Media Library", "Running", 41.7, "Failed", at, true),
            ],
            [
                new DiagnosticProblem(at, "Download or conversion failed", FailureInfo.From(new SourceUnavailableException($"AudioVault sign-in for {Email} failed: token={Token}") { HttpStatus = 403 })),
                new DiagnosticProblem(at, "Download or conversion failed", FailureInfo.From(new ConversionFailedException("ffmpeg exited with 1: /cache/audiovault/x/Bob's Burgers part1.mp3: Invalid data"))),
            ]);
    }

    [Fact]
    public void TitlesThatAreOrdinaryWordsDoNotGarblePluginText()
    {
        var r = new Redactor([], ["After", "Playback", "From", "Episodes", "Catalog"]);

        Assert.Equal("Playback address: http, Tailscale IP.", r.Scrub("Playback address: http, Tailscale IP.", titles: false));
        Assert.Equal("Full catalog sync", r.Scrub("Full catalog sync", titles: false));
        // Text from outside the plugin (errors, warnings) still loses every known title.
        Assert.DoesNotContain("Playback", r.Scrub("Could not convert Playback"), StringComparison.Ordinal);
    }
}
