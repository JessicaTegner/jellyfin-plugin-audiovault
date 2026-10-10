using System.Reflection;

namespace Jellyfin.Plugin.AudioVault.Services;

/// <summary>The Jellyfin members the plugin uses that aren't official plugin APIs, and whether they still bind.</summary>
public sealed record InternalsProbe(
    bool TmdbClient,
    bool TmdbSeasons,
    bool TmdbImageLanguages,
    bool PlaybackInfoAction,
    bool PlayStartSignal,
    bool SessionMessages,
    bool WebPath);

/// <summary>Jellyfin's TmdbClientManager members, bound by reflection. Season is null when its result can't be read.</summary>
public sealed record TmdbMembers(Type Client, MethodInfo Movie, MethodInfo Episode, MethodInfo? Season, MethodInfo? ImageLanguages);

/// <summary>
/// Finds the Jellyfin internals by name and signature. Types are compared by name only, so the same checks run on the
/// live server and, in tests, on a Jellyfin install read with a MetadataLoadContext.
/// </summary>
public static class JellyfinInternals
{
    public const string TmdbNamespace = "MediaBrowser.Providers.Plugins.Tmdb";

    private const string S = "System.String";
    private const string Ct = "System.Threading.CancellationToken";
    private const string I = "System.Int32";

    /// <summary>Assemblies as the running server has them.</summary>
    public static Assembly? LoadRunning(string name)
    {
        try
        {
            return AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == name) ?? Assembly.Load(name);
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or ArgumentException)
        {
            return null;
        }
    }

    public static InternalsProbe Probe(Func<string, Assembly?> load)
    {
        var tmdb = Safe(() => Tmdb(load));
        return new InternalsProbe(
            tmdb is not null,
            tmdb?.Season is not null,
            tmdb?.ImageLanguages is not null,
            Safe(() => PlaybackInfoAction(load)),
            Safe(() => PlayStartSignal(load)),
            Safe(() => SessionMessages(load)),
            Safe(() => Type(load, "MediaBrowser.Common", "MediaBrowser.Common.Configuration.IApplicationPaths")?.GetProperty("WebPath") is { } p
                && Name(p.PropertyType) == S));
    }

    /// <summary>The TMDb client's lookups, or null when the client or its movie and episode lookups don't match.</summary>
    public static TmdbMembers? Tmdb(Func<string, Assembly?> load)
    {
        var client = Type(load, "MediaBrowser.Providers", TmdbNamespace + ".TmdbClientManager");
        var movie = Method(client, "GetMovieAsync", [I, S, S, S, Ct]);
        var episode = Method(client, "GetEpisodeAsync", [I, I, "System.Int64", S, S, S, S, Ct]);
        if (client is null || movie is null || episode is null
            || !HasRuntime(TaskResult(movie)) || !HasRuntime(TaskResult(episode)))
        {
            return null;
        }

        var season = Method(client, "GetSeasonAsync", [I, I, S, S, S, Ct]);
        var episodes = TaskResult(season)?.GetProperty("Episodes");
        var item = episodes is null ? null : ElementType(episodes.PropertyType);
        var usableSeason = item?.GetProperty("EpisodeNumber") is { } number && Name(number.PropertyType) is "System.Int64" or I && HasRuntime(item);

        var utils = Type(load, "MediaBrowser.Providers", TmdbNamespace + ".TmdbUtils");
        var images = utils?.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == "GetImageLanguagesParam" && Matches(m, [S, S]) && Name(m.ReturnType) == S);
        return new TmdbMembers(client, movie, episode, usableSeason ? season : null, images);
    }

    /// <summary>PlaybackIntentFilter's hook: MediaInfoController's posted PlaybackInfo action, taking the item id.</summary>
    public static bool PlaybackInfoAction(Func<string, Assembly?> load) =>
        PostedPlaybackInfo(load) is { } action
        && action.GetParameters().Any(p => p.Name == "itemId" && Name(p.ParameterType) == "System.Guid")
        && Name(action.ReturnType).Contains("MediaBrowser.Model.MediaInfo.PlaybackInfoResponse", StringComparison.Ordinal);

    /// <summary>What tells a play from an information request, and which version it plays (PlaybackSignals).</summary>
    public static bool PlayStartSignal(Func<string, Assembly?> load)
    {
        if (PostedPlaybackInfo(load) is not { } action)
        {
            return false;
        }

        var parameters = action.GetParameters();
        bool Has(string name, params string[] types) =>
            parameters.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) && types.Contains(Name(p.ParameterType)))
            || parameters.Any(p => p.ParameterType.GetProperty(char.ToUpperInvariant(name[0]) + name[1..]) is { } prop && types.Contains(Name(prop.PropertyType)));
        return Has("autoOpenLiveStream", "System.Boolean", "System.Nullable<System.Boolean>") && Has("mediaSourceId", S);
    }

    /// <summary>What UserNotifier uses to show dialogs and stop players.</summary>
    public static bool SessionMessages(Func<string, Assembly?> load)
    {
        const string Asm = "MediaBrowser.Controller";
        var manager = Type(load, Asm, "MediaBrowser.Controller.Session.ISessionManager");
        var session = Type(load, Asm, "MediaBrowser.Controller.Session.SessionInfo");
        var controller = Type(load, Asm, "MediaBrowser.Controller.Session.ISessionController");
        return Method(manager, "SendMessageCommand", [S, S, "MediaBrowser.Model.Session.MessageCommand", Ct]) is not null
            && Method(manager, "SendPlaystateCommand", [S, S, "MediaBrowser.Model.Session.PlaystateRequest", Ct]) is not null
            && new[] { "SessionControllerConnected", "CapabilitiesChanged", "SessionActivity", "PlaybackStopped" }.All(e => manager!.GetEvent(e) is not null)
            && manager!.GetProperty("Sessions") is not null
            && new[] { "SessionControllers", "SupportedCommands", "NowPlayingItem", "Client", "DeviceId", "UserId", "LastActivityDate" }
                .All(p => session?.GetProperty(p) is not null)
            && controller?.GetProperty("IsSessionActive") is { } active && Name(active.PropertyType) == "System.Boolean";
    }

    /// <summary>A type's name with generic arguments written out, e.g. System.Nullable&lt;System.Int32&gt;.</summary>
    public static string Name(Type t)
    {
        if (!t.IsGenericType)
        {
            return t.FullName ?? t.Name;
        }

        var definition = t.GetGenericTypeDefinition().FullName ?? t.Name;
        var tick = definition.IndexOf('`', StringComparison.Ordinal);
        return (tick < 0 ? definition : definition[..tick]) + "<" + string.Join(",", t.GetGenericArguments().Select(Name)) + ">";
    }

    private static MethodInfo? PostedPlaybackInfo(Func<string, Assembly?> load) =>
        Type(load, "Jellyfin.Api", "Jellyfin.Api.Controllers.MediaInfoController")?
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "GetPostedPlaybackInfo");

    private static Type? Type(Func<string, Assembly?> load, string assembly, string name) => load(assembly)?.GetType(name);

    private static MethodInfo? Method(Type? type, string name, string[] parameters) =>
        type?.GetMethods(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault(m => m.Name == name && Matches(m, parameters));

    private static bool Matches(MethodInfo method, string[] parameters) =>
        method.GetParameters().Select(p => Name(p.ParameterType)).SequenceEqual(parameters, StringComparer.Ordinal);

    /// <summary>T of a Task&lt;T&gt; method.</summary>
    private static Type? TaskResult(MethodInfo? method) =>
        method?.ReturnType is { IsGenericType: true } r && Name(r.GetGenericTypeDefinition()).StartsWith("System.Threading.Tasks.Task", StringComparison.Ordinal)
            ? r.GetGenericArguments()[0]
            : null;

    private static bool HasRuntime(Type? type) =>
        type?.GetProperty("Runtime") is { } p && Name(p.PropertyType) is I or "System.Nullable<System.Int32>";

    private static Type? ElementType(Type t) =>
        t.IsArray
            ? t.GetElementType()
            : t.GetInterfaces().Prepend(t)
                .FirstOrDefault(i => i.IsGenericType && Name(i.GetGenericTypeDefinition()).StartsWith("System.Collections.Generic.IEnumerable", StringComparison.Ordinal))?
                .GetGenericArguments()[0];

    private static T? Safe<T>(Func<T> probe)
    {
        try
        {
            return probe();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A type that can't load (missing dependency, changed layout) counts as not binding.
            return default;
        }
    }
}
