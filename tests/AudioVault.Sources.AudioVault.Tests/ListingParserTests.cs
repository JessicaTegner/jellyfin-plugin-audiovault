using AudioVault.Core.Sources;

namespace AudioVault.Sources.AudioVault.Tests;

public class ListingParserTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public void ParsesMovieListing()
    {
        var page = ListingParser.ParseListing(Fixture("movies_page1.html"));

        Assert.Equal(100, page.Rows.Count);
        Assert.True(page.LastPage > 100);
        Assert.Equal(new ListingRow("22977", "'Twas the Night of the Lanterns (2021) [TTS]"), page.Rows[0]);
        Assert.All(page.Rows, r => Assert.Matches("^[0-9]+$", r.Id));
    }

    [Fact]
    public void ParsesShowListing()
    {
        var page = ListingParser.ParseListing(Fixture("shows_page1.html"));

        Assert.Equal(100, page.Rows.Count);
        Assert.Equal(new ListingRow("10867", "#offlineAF - Season 1 (2020)"), page.Rows[0]);
    }

    [Fact]
    public void ParsesRecentTables()
    {
        var (shows, movies) = ListingParser.ParseRecent(Fixture("home.html"));

        Assert.Equal(40, shows.Count);
        Assert.Equal(40, movies.Count);
    }

    [Fact]
    public void FindsCsrfToken() =>
        Assert.False(string.IsNullOrEmpty(ListingParser.ParseCsrfToken(Fixture("login.html"))));

    [Fact]
    public void MissingTableIsAFormatChange() =>
        Assert.Throws<SourceFormatChangedException>(() => ListingParser.ParseListing("<html><body><div>Maintenance</div></body></html>"));

    [Fact]
    public void ChangedRowLayoutIsAFormatChange()
    {
        const string html = """
            <table><tbody>
              <tr><td><a href="/watch/1">Batman (1989)</a></td></tr>
              <tr><td><a href="/watch/2">Alien (1979)</a></td></tr>
            </tbody></table>
            """;
        Assert.Throws<SourceFormatChangedException>(() => ListingParser.ParseListing(html));
    }

    [Fact]
    public void OneUnreadableTitleFailsTheFullListing()
    {
        // One changed download link among the fixture's 100 rows: skipping it would look like the title was removed.
        var html = Fixture("movies_page1.html").Replace("/download/14462", "/get/14462", StringComparison.Ordinal);
        Assert.Throws<SourceFormatChangedException>(() => ListingParser.ParseListing(html));
    }

    [Fact]
    public void RecentTablesStillSkipAnOddRow()
    {
        var html = Fixture("home.html").Replace("/download/27780", "/get/27780", StringComparison.Ordinal);
        var (shows, movies) = ListingParser.ParseRecent(html);

        Assert.Equal(79, shows.Count + movies.Count);
        Assert.DoesNotContain(shows.Concat(movies), r => r.Id == "27780");
    }

    [Fact]
    public void RowsWithoutATitleAreNotContent()
    {
        const string html = """
            <table><tbody>
              <tr><th>ID</th><th>Name</th><th></th></tr>
              <tr><td>1</td><td>Alien (1979)</td><td><a href="/download/1">Download</a></td></tr>
              <tr><td> </td><td></td><td></td></tr>
              <tr></tr>
              <tr><td colspan="3"><a href="?page=2">Next</a></td></tr>
              <tr><td>2</td><td>Batman (1989)</td><td><a href="/download/2">Download</a></td></tr>
            </tbody></table>
            """;
        var page = ListingParser.ParseListing(html);

        Assert.Equal(["1", "2"], page.Rows.Select(r => r.Id));
        Assert.Equal(2, page.LastPage);
    }

    [Fact]
    public void HomeWithoutRecentTablesIsAFormatChange() =>
        Assert.Throws<SourceFormatChangedException>(() => ListingParser.ParseRecent(Fixture("movies_page1.html")));
}
