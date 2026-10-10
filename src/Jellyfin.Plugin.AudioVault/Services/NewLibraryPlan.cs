namespace Jellyfin.Plugin.AudioVault.Services;

/// <summary>What the admin chose for the libraries "Create AudioVault libraries" makes. Everything on matches Jellyfin's own defaults.</summary>
public sealed class NewLibraryOptions
{
    public bool UseOmdb { get; set; } = true;

    public bool Posters { get; set; } = true;

    public bool Backdrops { get; set; } = true;

    public bool ExtraArtwork { get; set; } = true;

    public bool SeasonImages { get; set; } = true;

    public bool EpisodeImages { get; set; } = true;
}

/// <summary>Per item type settings; image types are Jellyfin ImageType names, kept as strings so this needs no Jellyfin types.</summary>
public sealed record TypePlan(string Type, string[] MetadataFetchers, string[] ImageFetchers, string[] DisabledImageTypes);

public static class NewLibraryPlan
{
    public const string Tmdb = "TheMovieDb";
    public const string Omdb = "The Open Movie Database";

    private static readonly string[] ExtraArtworkTypes = ["Art", "Banner", "Logo", "Thumb", "Disc"];
    private static readonly string[] AllArtworkTypes = ["Primary", "Backdrop", .. ExtraArtworkTypes];

    /// <summary>Image types left out here keep Jellyfin's defaults; disabled ones get a limit of 0.</summary>
    public static TypePlan[] For(bool movies, NewLibraryOptions options)
    {
        string[] types = movies ? ["Movie"] : ["Series", "Season", "Episode"];
        return types.Select(type => new TypePlan(
                type,
                movies && options.UseOmdb ? [Tmdb, Omdb] : [Tmdb],
                [Tmdb],
                DisabledImageTypes(type, options)))
            .ToArray();
    }

    private static string[] DisabledImageTypes(string type, NewLibraryOptions options)
    {
        if ((type == "Season" && !options.SeasonImages) || (type == "Episode" && !options.EpisodeImages))
        {
            return AllArtworkTypes;
        }

        var disabled = new List<string>();
        // Season and episode posters follow their own options.
        if (!options.Posters && type is "Movie" or "Series")
        {
            disabled.Add("Primary");
        }

        if (!options.Backdrops)
        {
            disabled.Add("Backdrop");
        }

        if (!options.ExtraArtwork)
        {
            disabled.AddRange(ExtraArtworkTypes);
        }

        return disabled.ToArray();
    }
}
