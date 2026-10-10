using Jellyfin.Plugin.AudioVault.Services;

namespace Jellyfin.Plugin.AudioVault.Tests;

public class NewLibraryPlanTests
{
    [Fact]
    public void Defaults_keep_all_of_Jellyfins_image_defaults()
    {
        var plans = NewLibraryPlan.For(false, new NewLibraryOptions()).Concat(NewLibraryPlan.For(true, new NewLibraryOptions()));

        Assert.All(plans, p => Assert.Empty(p.DisabledImageTypes));
        Assert.All(plans, p => Assert.Equal(["TheMovieDb"], p.ImageFetchers));
    }

    [Fact]
    public void Omdb_is_only_used_for_movies_and_only_when_on()
    {
        Assert.Equal(["TheMovieDb", "The Open Movie Database"], Assert.Single(NewLibraryPlan.For(true, new NewLibraryOptions())).MetadataFetchers);
        Assert.Equal(["TheMovieDb"], Assert.Single(NewLibraryPlan.For(true, new NewLibraryOptions { UseOmdb = false })).MetadataFetchers);
        Assert.All(NewLibraryPlan.For(false, new NewLibraryOptions()), p => Assert.Equal(["TheMovieDb"], p.MetadataFetchers));
    }

    [Fact]
    public void Shows_library_covers_series_seasons_and_episodes()
    {
        Assert.Equal(["Series", "Season", "Episode"], NewLibraryPlan.For(false, new NewLibraryOptions()).Select(p => p.Type));
    }

    [Fact]
    public void Posters_off_leaves_season_and_episode_pictures_alone()
    {
        var plans = NewLibraryPlan.For(false, new NewLibraryOptions { Posters = false }).ToDictionary(p => p.Type);

        Assert.Equal(["Primary"], plans["Series"].DisabledImageTypes);
        Assert.Empty(plans["Season"].DisabledImageTypes);
        Assert.Empty(plans["Episode"].DisabledImageTypes);
    }

    [Fact]
    public void Backdrops_and_extra_artwork_off()
    {
        var movie = Assert.Single(NewLibraryPlan.For(true, new NewLibraryOptions { Backdrops = false, ExtraArtwork = false }));

        Assert.Equal(["Backdrop", "Art", "Banner", "Logo", "Thumb", "Disc"], movie.DisabledImageTypes);
    }

    [Fact]
    public void Episode_images_off_disables_every_episode_image()
    {
        var plans = NewLibraryPlan.For(false, new NewLibraryOptions { EpisodeImages = false }).ToDictionary(p => p.Type);

        Assert.Contains("Primary", plans["Episode"].DisabledImageTypes);
        Assert.Contains("Thumb", plans["Episode"].DisabledImageTypes);
        Assert.Empty(plans["Season"].DisabledImageTypes);
        Assert.Empty(plans["Series"].DisabledImageTypes);
    }

    [Fact]
    public void Season_images_off_disables_every_season_image()
    {
        var season = NewLibraryPlan.For(false, new NewLibraryOptions { SeasonImages = false }).Single(p => p.Type == "Season");

        Assert.Equal(["Primary", "Backdrop", "Art", "Banner", "Logo", "Thumb", "Disc"], season.DisabledImageTypes);
    }
}
