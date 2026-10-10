using System.Reflection;
using System.Runtime.InteropServices;
using Jellyfin.Plugin.AudioVault.Services;

namespace Jellyfin.Plugin.AudioVault.Tests;

/// <summary>
/// The compatibility self-check, run against Jellyfin's assemblies. The packages this project references (Jellyfin.Controller
/// and Jellyfin.Model) are always checked. Set AUDIOVAULT_JELLYFIN_DIR to a Jellyfin server's program folder (the one
/// holding jellyfin.dll) to check everything, including MediaBrowser.Providers and Jellyfin.Api, which aren't packages.
/// </summary>
public sealed class JellyfinInternalsTests
{
    [Fact]
    public void ReferencedJellyfinPackagesHaveWhatUserNotifierUses()
    {
        var probe = JellyfinInternals.Probe(JellyfinInternals.LoadRunning);
        Assert.True(probe.SessionMessages, "ISessionManager, SessionInfo or ISessionController changed; UserNotifier's dialogs would fail");
        Assert.True(probe.WebPath, "IApplicationPaths.WebPath changed; the account menu link can't find config.json");
    }

    [Fact]
    public void MissingAssembliesCountAsNotBinding()
    {
        Assert.Equal(new InternalsProbe(false, false, false, false, false, false, false), JellyfinInternals.Probe(_ => null));
    }

    [Fact]
    public void TmdbLookupsBindToMatchingSignatures()
    {
        var members = JellyfinInternals.Tmdb(name => name == "MediaBrowser.Providers" ? typeof(MediaBrowser.Providers.Plugins.Tmdb.TmdbClientManager).Assembly : null);
        Assert.NotNull(members);
        Assert.Equal("GetSeasonAsync", members.Season?.Name);
        Assert.Equal("GetImageLanguagesParam", members.ImageLanguages?.Name);
    }

    [Theory]
    [InlineData(typeof(int?), "System.Nullable<System.Int32>")]
    [InlineData(typeof(Task<List<string>>), "System.Threading.Tasks.Task<System.Collections.Generic.List<System.String>>")]
    [InlineData(typeof(Guid), "System.Guid")]
    public void TypeNamesIgnoreAssemblyVersions(Type type, string expected)
    {
        Assert.Equal(expected, JellyfinInternals.Name(type));
    }

    [JellyfinInstallFact]
    public void JellyfinInstallStillHasEveryInternalThePluginUses()
    {
        var dir = JellyfinInstall.Directory!;
        Assert.True(File.Exists(Path.Combine(dir, "jellyfin.dll")), $"{JellyfinInstall.Variable}={dir} has no jellyfin.dll");
        using var context = JellyfinInstall.Open(dir);
        Assembly? Load(string name)
        {
            try
            {
                return context.LoadFromAssemblyName(name);
            }
            catch (FileNotFoundException)
            {
                return null;
            }
        }

        var probe = JellyfinInternals.Probe(Load);
        var version = Load("jellyfin")?.GetName().Version?.ToString() ?? "unknown";
        var issues = CompatibilityRules.Issues(new CompatibilityFacts(version, probe, null, MenuLinkCheck.Ok, null));
        Assert.True(issues.Count == 0, "Jellyfin internals no longer bind:\n" + string.Join("\n", issues.Select(i => $"[{i.Key}] {i.Message}")));
    }
}

/// <summary>Runs only when AUDIOVAULT_JELLYFIN_DIR names a Jellyfin install.</summary>
public sealed class JellyfinInstallFactAttribute : FactAttribute
{
    public JellyfinInstallFactAttribute()
    {
        if (JellyfinInstall.Directory is null)
        {
            Skip = $"Set {JellyfinInstall.Variable} to a Jellyfin server's program folder to check its internals";
        }
    }
}

internal static class JellyfinInstall
{
    public const string Variable = "AUDIOVAULT_JELLYFIN_DIR";

    public static string? Directory => Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } dir ? dir : null;

    /// <summary>Reads the install's assemblies without running them; the test's own runtime fills in what it lacks.</summary>
    public static MetadataLoadContext Open(string dir)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void AddAll(string? folder)
        {
            if (folder is null || !System.IO.Directory.Exists(folder))
            {
                return;
            }

            foreach (var file in System.IO.Directory.GetFiles(folder, "*.dll"))
            {
                files.TryAdd(Path.GetFileName(file), file);
            }
        }

        AddAll(dir);
        AddAll(RuntimeEnvironment.GetRuntimeDirectory());
        AddAll(Path.GetDirectoryName(typeof(Microsoft.AspNetCore.Mvc.ControllerBase).Assembly.Location));
        return new MetadataLoadContext(new PathAssemblyResolver(files.Values), "System.Private.CoreLib");
    }
}
