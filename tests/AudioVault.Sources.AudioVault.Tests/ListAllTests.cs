using System.Globalization;
using AudioVault.Core.Sources;

namespace AudioVault.Sources.AudioVault.Tests;

/// <summary>A full sync removes what it didn't see, so a page that silently went missing must fail the sync.</summary>
public class ListAllTests
{
    /// <param name="pages">Rows per page; the paginator links every other page, like Laravel's (the current one isn't a link).</param>
    /// <param name="malformed">Ids whose row has an unreadable download link, as after a partial layout change.</param>
    private static AudioVaultSource Site(int[] pages, Func<int, int>? claimedLastPage = null, int[]? malformed = null) =>
        SignInTests.Create(intercept: (request, _) =>
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath is not ("/movies" or "/shows"))
            {
                return Task.FromResult<HttpResponseMessage?>(null);
            }

            var page = int.Parse(uri.Query.Split('=')[1], CultureInfo.InvariantCulture);
            var last = claimedLastPage?.Invoke(page) ?? pages.Length;
            var rows = string.Concat(Enumerable.Range(0, page <= pages.Length ? pages[page - 1] : 0)
                .Select(i => (page * 1000) + i)
                .Select(id => $"<tr><td>{id}</td><td>Film {id} (2000)</td><td><a href=\"/{(malformed?.Contains(id) == true ? "get" : "download")}/{id}\">D</a></td></tr>"));
            var links = string.Concat(Enumerable.Range(1, last).Where(p => p != page).Select(p => $"<a href=\"?page={p}\">{p}</a>"));
            return Task.FromResult<HttpResponseMessage?>(FakeLaravel.Html($"<table><tbody>{rows}</tbody></table>{links}"));
        }).Source;

    private static async Task<int> CountAsync(AudioVaultSource source)
    {
        var count = 0;
        await foreach (var _ in source.ListAllAsync(null, default))
        {
            count++;
        }

        return count;
    }

    [Fact]
    public async Task ReadsEveryPage() =>
        Assert.Equal(2 * 25, await CountAsync(Site([10, 10, 5])));

    [Fact]
    public async Task EmptyLastPageIsAccepted() =>
        Assert.Equal(2 * 20, await CountAsync(Site([10, 10, 0])));

    [Fact]
    public async Task EmptyMiddlePageFailsTheSync() =>
        await Assert.ThrowsAsync<SourceFormatChangedException>(() => CountAsync(Site([10, 0, 10])));

    [Fact]
    public async Task WildlyChangingPageCountFailsTheSync() =>
        await Assert.ThrowsAsync<SourceFormatChangedException>(() => CountAsync(Site([10, 10, 10, 10, 10], p => p == 1 ? 5 : 40)));

    [Fact]
    public async Task ImplausiblePageCountFailsTheSync() =>
        await Assert.ThrowsAsync<SourceFormatChangedException>(() => CountAsync(Site([10, 10], _ => 5000)));

    [Fact]
    public async Task AnUnreadableTitleRowFailsTheSync()
    {
        // Title 2003 is indexed and still on AudioVault, but its row now reads differently: one in ten rows is not
        // a reason to report it removed.
        var source = Site([10, 10, 10]);
        Assert.Equal(60, await CountAsync(source));

        await Assert.ThrowsAsync<SourceFormatChangedException>(() => CountAsync(Site([10, 10, 10], malformed: [2003])));
    }

    [Fact]
    public async Task RecentListingToleratesAnUnreadableRow()
    {
        var home = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "home.html"))
            .Replace("/download/27780", "/get/27780", StringComparison.Ordinal);
        var source = SignInTests.Create(intercept: (request, _) => Task.FromResult<HttpResponseMessage?>(
            request.RequestUri!.AbsolutePath == "/" ? FakeLaravel.Html(home) : null)).Source;

        var ids = new List<string>();
        await foreach (var entry in source.ListRecentAsync(default))
        {
            ids.Add(entry.Id);
        }

        Assert.Equal(79, ids.Count);
        Assert.DoesNotContain("27780", ids);
    }
}
