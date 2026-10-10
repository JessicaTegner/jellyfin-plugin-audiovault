using AudioVault.Core.Sources;

namespace AudioVault.Sources.AudioVault.Tests;

public class NameParserTests
{
    [Theory]
    [InlineData("Batman (1989)", "Batman", 1989, "English", false, "")]
    [InlineData("'Tis the Season to be Irish (2024) [TTS]", "'Tis the Season to be Irish", 2024, "English", true, "")]
    [InlineData("28 Days Later [TTS] (2002)", "28 Days Later", 2002, "English", true, "")]
    [InlineData("But I'm a Cheerleader [Director's Cut] (1999)", "But I'm a Cheerleader", 1999, "English", false, "Director's Cut")]
    [InlineData("Star Trek 03: The Search for Spock (1984) [UK]", "Star Trek 03: The Search for Spock", 1984, "English", false, "UK")]
    [InlineData("Star Trek 01: The Motion Picture [US] (1979) [Director's Edition]", "Star Trek 01: The Motion Picture", 1979, "English", false, "US|Director's Edition")]
    [InlineData("Squad 36 (2025) [English]", "Squad 36", 2025, "English", false, "")]
    [InlineData("Some Film (2020) [French Description]", "Some Film", 2020, "French", false, "")]
    [InlineData("Some Film (2020) [Spanish description]", "Some Film", 2020, "Spanish", false, "")]
    [InlineData("Some Film (2020) [New US Description]", "Some Film", 2020, "English", false, "New US Description")]
    [InlineData("Heroin(e) (2017)", "Heroin(e)", 2017, "English", false, "")]
    [InlineData("Hearts Around the Table: Kiki's Fourth Ingredient (2025", "Hearts Around the Table: Kiki's Fourth Ingredient", 2025, "English", false, "")]
    [InlineData("Batman Ninja vs. Yakuza League 2025", "Batman Ninja vs. Yakuza League", 2025, "English", false, "")]
    [InlineData("Blade Runner 2049", "Blade Runner 2049", null, "English", false, "")]
    [InlineData("1917 (2019)", "1917", 2019, "English", false, "")]
    [InlineData("007_ Road To A Million (2023)", "007: Road To A Million", 2023, "English", false, "")]
    [InlineData("Zielona granica AKA Green Border (2023) [English]", "Zielona granica", 2023, "English", false, "")]
    [InlineData("Some Film (2001) [not described]", "Some Film", 2001, "English", false, "Not Described")]
    [InlineData("Some Film (2020) [Filipino Description]", "Some Film", 2020, "Filipino", false, "")]
    [InlineData("Some Film (2020) [Italian TTS Description]", "Some Film", 2020, "Italian", true, "")]
    [InlineData("Some Film (2020) [TTS Old Description]", "Some Film", 2020, "English", true, "Old Description")]
    [InlineData("Some Film (2020) [French TTS]", "Some Film", 2020, "French", true, "")]
    [InlineData("Some Film (2020) [Brazilian Portuguese Description]", "Some Film", 2020, "Portuguese", false, "")]
    [InlineData("Some Film (2020) [Mandarin Description]", "Some Film", 2020, "Chinese", false, "")]
    [InlineData("Some Film (2020) [German Description] [TTS]", "Some Film", 2020, "German", true, "")]
    [InlineData("Some Film (2020) [New Spanish Description]", "Some Film", 2020, "Spanish", false, "New Description")]
    [InlineData("Some Film (2020) [AudioVault Original]", "Some Film", 2020, "English", false, "AudioVault Original")]
    [InlineData("Some Film (2020) [UK]", "Some Film", 2020, "English", false, "UK")]
    public void ParsesMovies(string raw, string title, int? year, string language, bool tts, string tags)
    {
        var e = NameParser.Parse("1", MediaKind.Movie, raw);

        Assert.Equal(title, e.Title);
        Assert.Equal(year, e.Year);
        Assert.Equal(language, e.Version.Language);
        Assert.Equal(tts, e.Version.IsTts);
        Assert.Equal(tags, string.Join('|', e.Version.Tags));
        Assert.Null(e.Season);
        Assert.Equal(raw, e.RawName);
    }

    [Theory]
    [InlineData("Breaking Bad - Season 1 (2008)", "Breaking Bad", 1, null, 2008)]
    [InlineData("#blackAF - Season 1 (2020)", "#blackAF", 1, null, 2020)]
    [InlineData("24 Season 1 (2001) [not described]", "24", 1, null, 2001)]
    [InlineData("24 - Season 9 Live Another Day (2014)", "24", 9, null, 2014)]
    [InlineData("3rd Rock from the Sun - Season 5 (1999) mixed", "3rd Rock from the Sun", 5, null, 1999)]
    [InlineData("9-1-1: Lone Star - Season 1 (2020", "9-1-1: Lone Star", 1, null, 2020)]
    [InlineData("Moving On - Season 12(2021)", "Moving On", 12, null, 2021)]
    [InlineData("Monster: The Lyle and Erik Menendez Story Season 2 (2024)", "Monster: The Lyle and Erik Menendez Story", 2, null, 2024)]
    [InlineData("Money Heist - Season 5 Part 2 (2021) [English]", "Money Heist", 5, 2, 2021)]
    [InlineData("Money Heist - Part 3 (2019) [Spanish Description]", "Money Heist", 3, null, 2019)]
    [InlineData("You vs. Wild - season 1 (2019) [interactive]", "You vs. Wild", 1, null, 2019)]
    [InlineData("3Below - Tales of Arcadia - Season 2 (2019)", "3Below - Tales of Arcadia", 2, null, 2019)]
    [InlineData("1923 - Season 1 (2022)", "1923", 1, null, 2022)]
    [InlineData("#BringBackAlice (2023)", "#BringBackAlice", null, null, 2023)]
    public void ParsesShows(string raw, string title, int? season, int? part, int? year)
    {
        var e = NameParser.Parse("1", MediaKind.Show, raw);

        Assert.Equal(title, e.Title);
        Assert.Equal(season, e.Season);
        Assert.Equal(part, e.Part);
        Assert.Equal(year, e.Year);
    }

    [Fact]
    public void LanguageTagOnShowSetsVersionLanguage()
    {
        var e = NameParser.Parse("1", MediaKind.Show, "Money Heist - Part 3 (2019) [Spanish Description]");
        Assert.Equal("Spanish", e.Version.Language);
    }
}
