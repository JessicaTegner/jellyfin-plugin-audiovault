using Jellyfin.Plugin.AudioVault.Services;

namespace Jellyfin.Plugin.AudioVault.Tests;

public class FolderRulesTests
{
    private static readonly FolderContext Context = new(
        new FolderSet("/config/data/audiovault/library/movies", "/config/data/audiovault/library/shows", "/cache/audiovault"),
        ["/config", "/config/data", "/cache", "/config/data/audiovault", "/home/jellyfin"],
        ["/config/config", "/config/log", "/jellyfin/jellyfin-web", "/jellyfin", "/config/data/plugins", "/cache/temp"]);

    [Fact]
    public void EmptyFieldsUseDefaults()
    {
        var choice = FolderRules.Choose(null, " ", "", Context);

        Assert.Equal(Context.Defaults, choice.Folders);
        Assert.Empty(choice.Problems);
    }

    [Fact]
    public void SeparateFoldersAreUsedNormalized()
    {
        var choice = FolderRules.Choose("/media/av/movies/", " /media/av/shows", "/media/av-cache", Context);

        Assert.Equal(new FolderSet("/media/av/movies", "/media/av/shows", "/media/av-cache"), choice.Folders);
        Assert.Empty(choice.Problems);
    }

    [Theory]
    [InlineData("relative/movies")]
    [InlineData("/")]
    [InlineData("/home")]
    [InlineData("/media")]
    [InlineData("/usr")]
    [InlineData("/config")]
    [InlineData("/config/data")]
    [InlineData("/cache")]
    [InlineData("/home/jellyfin")]
    [InlineData("/config/data/audiovault")]
    [InlineData("/config/config")]
    [InlineData("/config/log/av")]
    [InlineData("/jellyfin/av")]
    public void UnsafeFoldersFallBackToTheDefault(string path)
    {
        var choice = FolderRules.Choose(path, null, null, Context);

        Assert.Equal(Context.Defaults.Movies, choice.Folders.Movies);
        Assert.Contains(path, Assert.Single(choice.Problems));
    }

    [Fact]
    public void SameFolderForMoviesAndShowsIsRejected()
    {
        var choice = FolderRules.Choose("/media/av", "/media/av", null, Context);

        Assert.NotEqual(choice.Folders.Movies, choice.Folders.Shows);
        Assert.Single(choice.Problems);
    }

    [Theory]
    [InlineData("/media/av", "/media/av/shows")]
    [InlineData("/media/av/movies", "/media/av")]
    public void NestedFoldersAreRejected(string movies, string shows)
    {
        var choice = FolderRules.Choose(movies, shows, null, Context);

        Assert.Single(choice.Problems);
        Assert.False(choice.Folders.Shows.StartsWith(choice.Folders.Movies + "/", StringComparison.Ordinal));
        Assert.False(choice.Folders.Movies.StartsWith(choice.Folders.Shows + "/", StringComparison.Ordinal));
    }

    [Fact]
    public void FolderContainingAnotherDefaultIsRejected()
    {
        // Would hold the default shows folder, whose files the movies sync would then remove.
        var choice = FolderRules.Choose("/config/data/audiovault/library", null, null, Context);

        Assert.Equal(Context.Defaults.Movies, choice.Folders.Movies);
        Assert.Single(choice.Problems);
    }

    [Fact]
    public void CacheMustNotOverlapLibraries()
    {
        var choice = FolderRules.Choose("/media/av/movies", null, "/media/av/movies/cache", Context);

        Assert.Equal(Context.Defaults.Cache, choice.Folders.Cache);
        Assert.Single(choice.Problems);
    }

    [Fact]
    public void ComparisonIgnoresCase()
    {
        var choice = FolderRules.Choose("/Media/AV", "/media/av", null, Context);

        Assert.Single(choice.Problems);
    }

    [Fact]
    public void SubfolderOfJellyfinDataIsAllowed()
    {
        var choice = FolderRules.Choose("/config/data/audiovault/movies2", null, null, Context);

        Assert.Empty(choice.Problems);
    }

    [Fact]
    public void SymlinkToTheOtherLibraryIsRejected()
    {
        using var dir = new LinkedFolders();
        var real = dir.Folder("shows");
        if (dir.Link("movies", real) is not { } alias)
        {
            return; // This platform can't create symlinks here.
        }

        var choice = FolderRules.Choose(alias, real, null, Context);

        // The shows folder is the movies folder under another name; it is the one checked against the other.
        Assert.Equal(Context.Defaults.Shows, choice.Folders.Shows);
        Assert.Single(choice.Problems);
    }

    [Fact]
    public void CacheInsideALinkToALibraryIsRejected()
    {
        using var dir = new LinkedFolders();
        var movies = dir.Folder("movies");
        if (dir.Link("alias", movies) is not { } alias)
        {
            return;
        }

        var choice = FolderRules.Choose(movies, null, Path.Combine(alias, "cache"), Context);

        Assert.Equal(Context.Defaults.Cache, choice.Folders.Cache);
        Assert.Single(choice.Problems);
    }

    [Fact]
    public void LinksIntoForbiddenFoldersAreRejected()
    {
        using var dir = new LinkedFolders();
        var config = dir.Folder("jellyfin-config");
        var context = Context with { Forbidden = [.. Context.Forbidden, config] };
        if (dir.Link("alias", config) is not { } alias)
        {
            return;
        }

        Assert.Single(FolderRules.Choose(alias, null, null, context).Problems);
        Assert.Single(FolderRules.Choose(Path.Combine(alias, "movies"), null, null, context).Problems);
    }

    [Fact]
    public void LinkToASeparateFolderIsAllowed()
    {
        using var dir = new LinkedFolders();
        var shows = dir.Folder("shows");
        if (dir.Link("movies", dir.Folder("elsewhere")) is not { } alias)
        {
            return;
        }

        Assert.Empty(FolderRules.Choose(alias, shows, null, Context).Problems);
    }

    private sealed class LinkedFolders : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "av-folders-" + Guid.NewGuid().ToString("N"));

        public LinkedFolders() => Directory.CreateDirectory(_root);

        public string Folder(string name) => Directory.CreateDirectory(Path.Combine(_root, name)).FullName;

        /// <summary>A symlink to <paramref name="target"/>, or null where symlinks can't be created (e.g. Windows without the right).</summary>
        public string? Link(string name, string target)
        {
            var path = Path.Combine(_root, name);
            try
            {
                Directory.CreateSymbolicLink(path, target);
                return path;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return null;
            }
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
