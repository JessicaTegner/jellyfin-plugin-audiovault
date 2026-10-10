using System.Globalization;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using AudioVault.Core.Sources;

namespace AudioVault.Sources.AudioVault;

internal sealed record ListingRow(string Id, string Name);

internal sealed record ListingPage(IReadOnlyList<ListingRow> Rows, int LastPage);

internal static class ListingParser
{
    private static readonly HtmlParser Parser = new();

    public static ListingPage ParseListing(string html)
    {
        var doc = Parser.ParseDocument(html);
        var table = doc.QuerySelector("table")
            ?? throw new SourceFormatChangedException("Listing page has no table");
        var rows = ParseRows(table, strict: true);
        var lastPage = doc.QuerySelectorAll("a[href*='page=']")
            .Select(a => PageNumber(a.GetAttribute("href")))
            .DefaultIfEmpty(1)
            .Max();
        return new ListingPage(rows, lastPage);
    }

    /// <summary>The home page lists recent uploads in tables headed "Recent Shows:" and "Recent Movies:".</summary>
    public static (IReadOnlyList<ListingRow> Shows, IReadOnlyList<ListingRow> Movies) ParseRecent(string html)
    {
        var doc = Parser.ParseDocument(html);
        IReadOnlyList<ListingRow>? shows = null, movies = null;
        foreach (var table in doc.QuerySelectorAll("table"))
        {
            var heading = PrecedingHeading(table);
            if (heading.Contains("recent shows", StringComparison.OrdinalIgnoreCase))
            {
                shows = ParseRows(table, strict: false);
            }
            else if (heading.Contains("recent movies", StringComparison.OrdinalIgnoreCase))
            {
                movies = ParseRows(table, strict: false);
            }
        }

        if (shows is null || movies is null)
        {
            throw new SourceFormatChangedException("Home page no longer has the Recent Shows/Movies tables");
        }

        return (shows, movies);
    }

    public static string? ParseCsrfToken(string html) =>
        Parser.ParseDocument(html).QuerySelector("input[name='_token']")?.GetAttribute("value");

    /// <param name="strict">
    /// True for the full listing, where a skipped title would look removed from AudioVault: any content row that can't
    /// be read fails the page. The recent tables only add titles, so there a few odd rows are skipped.
    /// </param>
    private static List<ListingRow> ParseRows(IElement table, bool strict)
    {
        var rows = new List<ListingRow>();
        var bad = 0;
        var content = 0;
        foreach (var tr in table.QuerySelectorAll("tbody tr"))
        {
            var cells = tr.QuerySelectorAll("td");
            var link = tr.QuerySelector("a[href*='/download/']")?.GetAttribute("href");
            if (!IsContentRow(cells, link))
            {
                continue;
            }

            content++;
            var id = cells.Length >= 2 ? cells[0].TextContent.Trim() : string.Empty;
            var name = cells.Length >= 2 ? cells[1].TextContent.Trim() : string.Empty;
            if (id.Length == 0 || !id.All(char.IsAsciiDigit) || name.Length == 0
                || link is null || !link.TrimEnd('/').EndsWith("/" + id, StringComparison.Ordinal))
            {
                if (strict)
                {
                    throw new SourceFormatChangedException($"Listing row {content} could not be parsed: {Shorten(tr.TextContent)}");
                }

                bad++;
                continue;
            }

            rows.Add(new ListingRow(id, name));
        }

        // An empty table is legitimate (e.g. search past the end); mostly-broken rows are not.
        if (bad > 0 && bad > content / 10)
        {
            throw new SourceFormatChangedException($"{bad} of {content} listing rows could not be parsed");
        }

        return rows;
    }

    /// <summary>
    /// Header, blank and full-width message or pagination rows (no download link, no cells or one spanning cell) carry
    /// no title. Anything else is meant to be a title, readable or not.
    /// </summary>
    private static bool IsContentRow(IHtmlCollection<IElement> cells, string? downloadLink) =>
        downloadLink is not null
        || (cells.Length > 0
            && cells.Any(c => !string.IsNullOrWhiteSpace(c.TextContent))
            && !(cells.Length == 1 && cells[0].HasAttribute("colspan")));

    private static string Shorten(string text)
    {
        var flat = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= 80 ? flat : flat[..80] + "...";
    }

    private static string PrecedingHeading(IElement element)
    {
        for (var node = element.PreviousElementSibling; node is not null; node = node.PreviousElementSibling)
        {
            if (node.LocalName is "h1" or "h2" or "h3" or "h4" or "h5" or "h6")
            {
                return node.TextContent;
            }
        }

        return element.ParentElement is { } parent ? PrecedingHeading(parent) : string.Empty;
    }

    private static int PageNumber(string? href)
    {
        if (href is null)
        {
            return 1;
        }

        var query = href.Contains('?', StringComparison.Ordinal) ? href[(href.IndexOf('?', StringComparison.Ordinal) + 1)..] : href;
        foreach (var pair in query.Replace("&amp;", "&", StringComparison.Ordinal).Split('&'))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length == 2 && kv[0] == "page" && int.TryParse(kv[1], NumberStyles.None, CultureInfo.InvariantCulture, out var page))
            {
                return page;
            }
        }

        return 1;
    }
}
