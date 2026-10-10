// Stand-ins with the names and signatures JellyfinInternals looks for in MediaBrowser.Providers and TMDbLib.
namespace MediaBrowser.Providers.Plugins.Tmdb;

public sealed class TmdbClientManager
{
    public Task<FakeMovie?> GetMovieAsync(int tmdbId, string? language, string? imageLanguages, string? countryCode, CancellationToken ct) =>
        Task.FromResult<FakeMovie?>(null);

    public Task<FakeEpisode?> GetEpisodeAsync(int seriesTmdbId, int season, long episode, string? order, string? language, string? imageLanguages, string? countryCode, CancellationToken ct) =>
        Task.FromResult<FakeEpisode?>(null);

    public Task<FakeSeason?> GetSeasonAsync(int seriesTmdbId, int season, string? language, string? imageLanguages, string? countryCode, CancellationToken ct) =>
        Task.FromResult<FakeSeason?>(null);
}

public static class TmdbUtils
{
    public static string GetImageLanguagesParam(string preferredLanguage, string? countryCode) => preferredLanguage;
}

public class FakeMovie
{
    public int? Runtime { get; set; }
}

public sealed class FakeEpisode : FakeMovie;

public sealed class FakeSeason
{
    public List<FakeSeasonEpisode> Episodes { get; set; } = [];
}

public sealed class FakeSeasonEpisode
{
    public long EpisodeNumber { get; set; }

    public int? Runtime { get; set; }
}
