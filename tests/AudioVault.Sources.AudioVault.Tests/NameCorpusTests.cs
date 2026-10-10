using System.Text.RegularExpressions;
using AudioVault.Core.Sources;

namespace AudioVault.Sources.AudioVault.Tests;

public class NameCorpusTests
{
    public static TheoryData<string> Names()
    {
        var data = new TheoryData<string>();
        foreach (var line in File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "names.txt")))
        {
            if (line.Length > 0)
            {
                data.Add(line);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void CorpusNamesParseCleanly(string raw)
    {
        var kind = Regex.IsMatch(raw, @"\bseason\b", RegexOptions.IgnoreCase) ? MediaKind.Show : MediaKind.Movie;
        var e = NameParser.Parse("1", kind, raw);

        Assert.False(string.IsNullOrWhiteSpace(e.Title));
        Assert.DoesNotContain("[", e.Title, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"\((?:19|20)\d{2}", e.Title);
        Assert.DoesNotMatch(@"(?i)\bseason\s*\d", e.Title);
    }
}
