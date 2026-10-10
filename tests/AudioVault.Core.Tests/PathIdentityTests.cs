using System.Diagnostics;
using AudioVault.Core.Catalog;
using AudioVault.Core.Library;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioVault.Core.Tests;

public class PathIdentityTests
{
    [Fact]
    public void SymlinkedFolderIsTheSameFolder()
    {
        using var dir = new TempDir();
        var real = Directory.CreateDirectory(Path.Combine(dir.Path, "shows")).FullName;
        var link = Path.Combine(dir.Path, "movies");
        Directory.CreateSymbolicLink(link, real);

        Assert.True(PathIdentity.Same(link, real));
        Assert.True(PathIdentity.Overlaps(link, real));
        Assert.Equal(PathIdentity.Physical(real), PathIdentity.Physical(link));
    }

    [Fact]
    public void LinkIntoAnotherFolderIsInsideIt()
    {
        using var dir = new TempDir();
        var lib = Directory.CreateDirectory(Path.Combine(dir.Path, "lib")).FullName;
        Directory.CreateDirectory(Path.Combine(lib, "shows"));
        var link = Path.Combine(dir.Path, "alias");
        Directory.CreateSymbolicLink(link, Path.Combine(lib, "shows"));

        Assert.True(PathIdentity.SameOrInside(link, lib));
        Assert.False(PathIdentity.SameOrInside(lib, link));
        Assert.True(PathIdentity.Overlaps(lib, link));
    }

    [Fact]
    public void NonexistentSuffixBelowALinkIsKept()
    {
        using var dir = new TempDir();
        var real = Directory.CreateDirectory(Path.Combine(dir.Path, "real")).FullName;
        var link = Path.Combine(dir.Path, "link");
        Directory.CreateSymbolicLink(link, real);

        Assert.Equal(
            Path.Combine(PathIdentity.Physical(real), "not", "yet"),
            PathIdentity.Physical(Path.Combine(link, "not", "yet") + Path.DirectorySeparatorChar));
        Assert.True(PathIdentity.SameOrInside(Path.Combine(link, "not", "yet"), real));
    }

    [Fact]
    public void ChainedAndRelativeLinksResolve()
    {
        using var dir = new TempDir();
        var real = Directory.CreateDirectory(Path.Combine(dir.Path, "real")).FullName;
        var first = Path.Combine(dir.Path, "first");
        Directory.CreateSymbolicLink(first, "real");
        var second = Path.Combine(dir.Path, "second");
        Directory.CreateSymbolicLink(second, first);

        Assert.True(PathIdentity.Same(second, real));
    }

    [Fact]
    public void SiblingsWithACommonPrefixDoNotOverlap()
    {
        using var dir = new TempDir();
        Assert.False(PathIdentity.Overlaps(Path.Combine(dir.Path, "av"), Path.Combine(dir.Path, "av-shows")));
        Assert.True(PathIdentity.SameOrInside(Path.Combine(dir.Path, "av") + Path.DirectorySeparatorChar, Path.Combine(dir.Path, "av")));
    }

    [Fact]
    public void LinkLoopDoesNotHang()
    {
        using var dir = new TempDir();
        var a = Path.Combine(dir.Path, "a");
        var b = Path.Combine(dir.Path, "b");
        Directory.CreateSymbolicLink(a, b);
        Directory.CreateSymbolicLink(b, a);

        _ = PathIdentity.Physical(Path.Combine(a, "x"));
    }

    [Fact]
    public void MacTmpAliasIsResolved()
    {
        if (!OperatingSystem.IsMacOS() || !Directory.Exists("/private/tmp"))
        {
            return;
        }

        Assert.True(PathIdentity.Same("/tmp/av-movies", "/private/tmp/av-movies"));
    }

    [Fact]
    public void SymlinkedLibraryFoldersAreRefused()
    {
        using var dir = new TempDir();
        var shows = Directory.CreateDirectory(Path.Combine(dir.Path, "shows")).FullName;
        var movies = Path.Combine(dir.Path, "movies");
        Directory.CreateSymbolicLink(movies, shows);

        Assert.Throws<LibraryConfigurationException>(() =>
            LibrarySyncService.ValidateFolders(new LibraryOptions { MoviesPath = movies, ShowsPath = shows }));
    }

    [Fact]
    public void SymlinkIntoTheOtherLibraryIsRefused()
    {
        using var dir = new TempDir();
        var shows = Directory.CreateDirectory(Path.Combine(dir.Path, "shows")).FullName;
        Directory.CreateDirectory(Path.Combine(shows, "sub"));
        var movies = Path.Combine(dir.Path, "movies");
        Directory.CreateSymbolicLink(movies, Path.Combine(shows, "sub"));

        var service = new LibrarySyncService(
            new CatalogStore(dir.Path),
            new EpisodeCountStore(dir.Path),
            new LibraryWriter(NullLogger<LibraryWriter>.Instance),
            NullLogger<LibrarySyncService>.Instance);
        Assert.Throws<LibraryConfigurationException>(() =>
            service.Sync(new LibraryOptions { MoviesPath = movies, ShowsPath = shows }, (id, ep) => id));
    }

    [Fact]
    public void JunctionIsTheSameFolder()
    {
        // Junctions exist only on Windows; elsewhere symlinks cover the same code path.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var dir = new TempDir();
        var real = Directory.CreateDirectory(Path.Combine(dir.Path, "real")).FullName;
        var junction = Path.Combine(dir.Path, "junction");
        using (var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junction}\" \"{real}\"")
               {
                   UseShellExecute = false,
                   CreateNoWindow = true,
               })!)
        {
            mklink.WaitForExit();
        }

        Assert.True(Directory.Exists(junction));
        Assert.True(PathIdentity.Same(junction, real));
        Assert.True(PathIdentity.Overlaps(Path.Combine(junction, "x"), real));
    }
}
