using System.Collections;
using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AudioVault.Services;

public interface IRuntimeLookup
{
    bool Available { get; }

    Task<TimeSpan?> MovieAsync(int tmdbId, string? language, string? countryCode, CancellationToken ct);

    Task<TimeSpan?> EpisodeAsync(int seriesTmdbId, int season, int episode, string displayOrder, string? language, string? countryCode, CancellationToken ct);
}

public interface ISeasonEpisodeLookup
{
    /// <summary>
    /// The season's episode numbers in TMDb's default (aired) order, without specials. Empty when TMDb lists none;
    /// null when Jellyfin's TMDb client can't be used, so the caller falls back to Jellyfin's providers.
    /// </summary>
    Task<IReadOnlyList<int>?> SeasonEpisodesAsync(int seriesTmdbId, int season, string? language, string? countryCode, CancellationToken ct);
}

/// <summary>
/// Reads runtimes and season episode lists through Jellyfin's own TmdbClientManager (its API key, its one-hour
/// response cache). That class lives in MediaBrowser.Providers, which plugins can't reference, so it is reached by
/// reflection; if anything doesn't match this Jellyfin version, lookups quietly return null.
/// </summary>
public sealed class JellyfinTmdbRuntimeLookup(IServiceProvider services, ILogger<JellyfinTmdbRuntimeLookup> logger) : IRuntimeLookup, ISeasonEpisodeLookup
{
    private const string Namespace = "MediaBrowser.Providers.Plugins.Tmdb";
    private const string Unavailable =
        "TMDb lookups unavailable on this Jellyfin version; unplayed titles will show no runtime and episode lists are looked up one episode at a time";

    private static readonly TimeSpan ClientRetry = TimeSpan.FromMinutes(5);

    private readonly object _lock = new();
    private Binding? _binding;
    private bool _failed;
    private object? _client;
    private DateTimeOffset _nextClientAttempt;
    private bool _clientWarned;

    public bool Available => Bind() is not null;

    public async Task<TimeSpan?> MovieAsync(int tmdbId, string? language, string? countryCode, CancellationToken ct)
    {
        if (Bind() is not { } b || Client(b) is not { } client)
        {
            return null;
        }

        var movie = await InvokeAsync(b.Movie, client, [tmdbId, language, b.ImageLanguages(language, countryCode), countryCode, ct]).ConfigureAwait(false);
        return RuntimeRules.FromMinutes(ReadRuntime(movie));
    }

    public async Task<TimeSpan?> EpisodeAsync(
        int seriesTmdbId,
        int season,
        int episode,
        string displayOrder,
        string? language,
        string? countryCode,
        CancellationToken ct)
    {
        if (Bind() is not { } b || Client(b) is not { } client)
        {
            return null;
        }

        var images = b.ImageLanguages(language, countryCode);
        var found = await InvokeAsync(b.Episode, client, [seriesTmdbId, season, (long)episode, displayOrder, language, images, countryCode, ct])
            .ConfigureAwait(false);
        if (RuntimeRules.FromMinutes(ReadRuntime(found)) is { } runtime)
        {
            return runtime;
        }

        // Alternate display orders renumber episodes, so the plain season list only matches the default order.
        if (b.Season is null || displayOrder.Length > 0)
        {
            return null;
        }

        var seasonInfo = await InvokeAsync(b.Season, client, [seriesTmdbId, season, language, images, countryCode, ct]).ConfigureAwait(false);
        if (seasonInfo?.GetType().GetProperty("Episodes")?.GetValue(seasonInfo) is not IEnumerable episodes)
        {
            return null;
        }

        foreach (var e in episodes)
        {
            if (e?.GetType().GetProperty("EpisodeNumber")?.GetValue(e) is { } number
                && Convert.ToInt64(number, System.Globalization.CultureInfo.InvariantCulture) == episode)
            {
                return RuntimeRules.FromMinutes(ReadRuntime(e));
            }
        }

        return null;
    }

    public async Task<IReadOnlyList<int>?> SeasonEpisodesAsync(int seriesTmdbId, int season, string? language, string? countryCode, CancellationToken ct)
    {
        if (Bind() is not { Season: { } method } b || Client(b) is not { } client)
        {
            return null;
        }

        // TvSeason.Episodes: List<TMDbLib.Objects.Search.TvSeasonEpisode>, each with a long EpisodeNumber.
        var seasonInfo = await InvokeAsync(method, client, [seriesTmdbId, season, language, b.ImageLanguages(language, countryCode), countryCode, ct])
            .ConfigureAwait(false);
        if (seasonInfo is null)
        {
            // TMDb doesn't have this season.
            return [];
        }

        if (seasonInfo.GetType().GetProperty("Episodes") is not { } property)
        {
            return null;
        }

        if (property.GetValue(seasonInfo) is not IEnumerable episodes)
        {
            return [];
        }

        var numbers = new List<long>();
        foreach (var e in episodes)
        {
            if (e?.GetType().GetProperty("EpisodeNumber")?.GetValue(e) is { } number)
            {
                numbers.Add(Convert.ToInt64(number, System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        return EpisodeListRules.EpisodeNumbers(numbers);
    }

    private static int? ReadRuntime(object? value) =>
        value?.GetType().GetProperty("Runtime")?.GetValue(value) is int minutes ? minutes : null;

    private static async Task<object?> InvokeAsync(MethodInfo method, object target, object?[] args)
    {
        if (method.Invoke(target, args) is not Task task)
        {
            return null;
        }

        await task.ConfigureAwait(false);
        return task.GetType().GetProperty("Result")?.GetValue(task);
    }

    private Binding? Bind()
    {
        lock (_lock)
        {
            if (_binding is not null || _failed)
            {
                return _binding;
            }

            try
            {
                // CompatibilityCheck explains on the settings page what a failed binding costs.
                if (JellyfinInternals.Tmdb(JellyfinInternals.LoadRunning) is not { } members)
                {
                    logger.LogDebug(Unavailable);
                    _failed = true;
                    return null;
                }

                _binding = new Binding(members.Client, members.Movie, members.Episode, members.Season, members.ImageLanguages);
                return _binding;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, Unavailable);
                _failed = true;
                return null;
            }
        }
    }

    /// <summary>Resolved on first use: the TMDb plugin may not be set up yet while our services start.</summary>
    private object? Client(Binding binding)
    {
        lock (_lock)
        {
            if (_client is not null || DateTimeOffset.UtcNow < _nextClientAttempt)
            {
                return _client;
            }

            try
            {
                _client = services.GetService(binding.ClientType);
            }
            catch (Exception ex)
            {
                if (!_clientWarned)
                {
                    logger.LogWarning(ex, "Jellyfin's TMDb client is not available; TMDb lookups will retry later");
                    _clientWarned = true;
                }
            }

            if (_client is null)
            {
                _nextClientAttempt = DateTimeOffset.UtcNow + ClientRetry;
            }

            return _client;
        }
    }

    private sealed record Binding(Type ClientType, MethodInfo Movie, MethodInfo Episode, MethodInfo? Season, MethodInfo? ImageLanguagesMethod)
    {
        // Same value Jellyfin's providers pass: the cache key ignores it, so a different one would change their cached images.
        public string? ImageLanguages(string? language, string? countryCode)
        {
            try
            {
                return ImageLanguagesMethod?.Invoke(null, [language ?? string.Empty, countryCode]) as string;
            }
            catch (TargetInvocationException)
            {
                return null;
            }
        }
    }
}
