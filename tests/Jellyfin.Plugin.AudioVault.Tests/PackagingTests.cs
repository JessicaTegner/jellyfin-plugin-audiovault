using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.AudioVault.Tests;

/// <summary>
/// Jellyfin offers and loads a plugin only on servers at least as new as its targetAbi. Built against Jellyfin.Controller
/// x.y.z, the plugin needs at least that server, so targetAbi must be exactly that version: lower lets older servers
/// install a plugin that fails to load, higher hides it from servers it would run on.
/// </summary>
public sealed partial class PackagingTests
{
    private static readonly string Root = FindRoot();

    [Fact]
    public void TargetAbiMatchesTheJellyfinPackagesBuiltAgainst()
    {
        var abi = Version.Parse(Required(TargetAbi().Match(File.ReadAllText(Path.Combine(Root, "build.yaml"))), "targetAbi in build.yaml"));
        var project = File.ReadAllText(Path.Combine(Root, "src/Jellyfin.Plugin.AudioVault/Jellyfin.Plugin.AudioVault.csproj"));
        var props = File.ReadAllText(Path.Combine(Root, "Directory.Build.props"));
        var jellyfin = Version.Parse(Required(JellyfinVersion().Match(props), "JellyfinVersion in Directory.Build.props"));

        Assert.Equal(4, abi.ToString().Split('.').Length);
        Assert.Equal(new Version(jellyfin.Major, jellyfin.Minor, Math.Max(0, jellyfin.Build), 0), abi);
        Assert.Contains("Include=\"Jellyfin.Controller\" Version=\"$(JellyfinVersion)\"", project);
        Assert.Contains("Include=\"Jellyfin.Model\" Version=\"$(JellyfinVersion)\"", project);
    }

    // A push to gh-pages with the workflow's token doesn't publish Pages, so the workflow deploys the folder itself.
    [Fact]
    public void ReleasePublishesPagesThroughDeployPages()
    {
        var workflow = File.ReadAllText(Path.Combine(Root, ".github/workflows/release.yml"));

        Assert.Contains("uses: actions/upload-pages-artifact@", workflow);
        Assert.Contains("uses: actions/deploy-pages@", workflow);
        Assert.Contains("pages: write", workflow);
        Assert.Contains("id-token: write", workflow);
        Assert.Contains("contents: write", workflow);
        Assert.Matches(@"environment:\s+name: github-pages\s+url: \$\{\{ steps\.deployment\.outputs\.page_url \}\}", workflow);
        Assert.Contains("path: pages", workflow);
    }

    // After a partial failure a rerun must finish the missing steps instead of stopping at the existing release.
    [Fact]
    public void ReleaseStepsCanBeRerun()
    {
        var workflow = File.ReadAllText(Path.Combine(Root, ".github/workflows/release.yml"));

        Assert.Matches(@"if ! gh release view ""\$GITHUB_REF_NAME"" >/dev/null 2>&1; then[\s\S]*?gh release create[\s\S]*?\n\s*fi\n", workflow);
        Assert.Matches(@"gh release upload ""\$GITHUB_REF_NAME"" .* --clobber", workflow);
        Assert.Matches(@"if git diff --cached --quiet; then[\s\S]*?else[\s\S]*?commit[\s\S]*?fi", workflow);
        Assert.Single(Regex.Matches(workflow, "gh release create"));
        Assert.Contains("name: github-pages-${{ github.run_attempt }}", workflow);
        Assert.Contains("artifact_name: github-pages-${{ github.run_attempt }}", workflow);
    }

    [Fact]
    public async Task ParserFixturesMatchTheirGenerator()
    {
        var start = new ProcessStartInfo("python3", ["-I", Path.Combine(Root, "scripts/make_fixtures.py"), "--check"])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var python = Process.Start(start)!;
        var output = python.StandardOutput.ReadToEndAsync();
        var errors = await python.StandardError.ReadToEndAsync();
        await python.WaitForExitAsync();

        Assert.True(python.ExitCode == 0, errors + await output);
        Assert.StartsWith("All ", await output, StringComparison.Ordinal);
    }

    private static string Required(Match m, string what) => m.Success ? m.Groups[1].Value : throw new InvalidOperationException(what + " not found");

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "build.yaml")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("build.yaml not found above " + AppContext.BaseDirectory);
    }

    [GeneratedRegex(@"(?m)^targetAbi:\s*""?([0-9.]+)""?\s*$")]
    private static partial Regex TargetAbi();

    [GeneratedRegex(@"<JellyfinVersion Condition=""[^""]*"">([0-9.]+)</JellyfinVersion>")]
    private static partial Regex JellyfinVersion();
}
