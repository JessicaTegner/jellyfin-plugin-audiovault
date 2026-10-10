using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AudioVault.Core.Sources;

namespace Jellyfin.Plugin.AudioVault.Services;

public sealed record DiagnosticServer(string PluginVersion, string JellyfinVersion, string Os, string Architecture, string DotNet, string? Container);

/// <param name="LastFailed">Jellyfin keeps only the error's text, which the report leaves out.</param>
public sealed record DiagnosticTask(string Name, string State, double? Progress, string? LastStatus, DateTimeOffset? LastEnd, bool LastFailed);

public sealed record DiagnosticProblem(DateTimeOffset At, string Kind, FailureInfo? Failure);

/// <param name="Movies">Counts from the catalog, the written library files and Jellyfin.</param>
public sealed record DiagnosticCounts(
    int Titles,
    int Movies,
    int ShowSeasons,
    bool FullListing,
    int MovieFiles,
    int EpisodeFiles,
    int? MoviesInJellyfin,
    int? EpisodesInJellyfin,
    int? MoviesSearchable,
    int? EpisodesSearchable,
    EpisodeListStatus? EpisodeLists);

public sealed record DiagnosticDownloads(int Last24hAllAccounts, int LimitPerAccount, int Active, DateTimeOffset? BlockedUntil, long CacheBytes, int CacheLimitGb);

public sealed record DiagnosticSettings(
    string PublicUrl,
    string PublicUrlSource,
    bool FlareSolverrConfigured,
    string PreferredLanguage,
    bool IncludeOtherLanguages,
    bool IncludeTts,
    int AudioBitrateKbps,
    bool EvictWhenLowOnSpace,
    bool ShowAccountMenuLink,
    bool SignOutDeletedUsers,
    int UsersWithAccount,
    int Users);

/// <param name="SourceOk">The AudioVault connection check; null when it wasn't run.</param>
public sealed record DiagnosticFacts(
    DateTimeOffset At,
    DiagnosticServer Server,
    IReadOnlyList<SetupStep>? Setup,
    IReadOnlyList<CompatibilityIssue> Compatibility,
    string? PartHealth,
    IReadOnlyList<string> Warnings,
    bool? SourceOk,
    string? SourceMessage,
    FailureInfo? SourceFailure,
    DiagnosticCounts? Counts,
    DiagnosticDownloads Downloads,
    DiagnosticSettings Settings,
    int UnreachedPlayers,
    IReadOnlyList<DiagnosticTask> Tasks,
    IReadOnlyList<DiagnosticProblem> RecentProblems);

/// <summary>
/// The plain-text report an admin pastes into a bug report. Errors are given as categories, never their text; the remaining
/// free text (the plugin's own messages) goes through the redactor.
/// </summary>
public static partial class DiagnosticReport
{
    public static string Build(DiagnosticFacts f, Redactor r)
    {
        var sb = new StringBuilder();
        void Line(string text) => sb.Append(text).Append('\n');
        void Item(string label, string? value) => Line($"- {label}: {r.Scrub(value)}");
        void Heading(string text)
        {
            sb.Append('\n');
            Line(text);
        }

        Line("AudioVault diagnostic report");
        Line("Generated " + Time(f.At) + ". Names, emails, titles, addresses, paths and tokens are removed as far as they can be recognised; read it before posting.");

        Heading("Versions");
        // Version numbers would read as IP addresses to the redactor.
        Line("- Plugin: " + f.Server.PluginVersion);
        Line("- Jellyfin: " + f.Server.JellyfinVersion);
        // The runtime's OS description (kernel name, version and build) names nothing personal; scrubbing took it for a path.
        Line($"- Operating system: {OsName(f.Server.Os)}");
        Item("Architecture", f.Server.Architecture);
        Item(".NET", f.Server.DotNet);
        Item("Container", f.Server.Container ?? "none detected");

        Heading("Setup checklist");
        if (f.Setup is null)
        {
            Line("- Could not be worked out.");
        }
        else
        {
            foreach (var step in f.Setup)
            {
                Line($"- {step.Title}: {State(step.State)}. {r.Scrub(StepDetail(step, f), titles: false)}");
            }
        }

        Heading("Compatibility");
        if (f.Compatibility.Count == 0)
        {
            Line("- Everything the plugin uses from Jellyfin is available.");
        }

        foreach (var issue in f.Compatibility)
        {
            Line($"- {(issue.Severe ? "Problem" : "Limited")} ({issue.Key}): {r.Scrub(issue.Message, titles: false)}");
        }

        Heading("Health");
        Item("Plugin parts", f.PartHealth ?? "all started");
        Line("- AudioVault connection: " + f.SourceOk switch
        {
            null => "not checked",
            true => r.Scrub(f.SourceMessage),
            false => "problem: " + (f.SourceFailure is { } failure ? Describe(failure) : r.Scrub(f.SourceMessage)),
        });
        Line($"- Players that couldn't load the playback address: {N(f.UnreachedPlayers)}");
        if (f.Warnings.Count == 0)
        {
            Line("- Warnings: none");
        }

        foreach (var warning in f.Warnings)
        {
            Item("Warning", warning.Replace("{at}", "a recent time", StringComparison.Ordinal));
        }

        Heading("Catalog and libraries");
        if (f.Counts is not { } c)
        {
            Line("- Could not be counted.");
        }
        else
        {
            Line($"- Catalog: {N(c.Titles)} titles ({N(c.Movies)} movies, {N(c.ShowSeasons)} show seasons), full listing read: {YesNo(c.FullListing)}");
            Line($"- Library files: {N(c.MovieFiles)} movies, {N(c.EpisodeFiles)} episodes");
            Line($"- In Jellyfin: {N(c.MoviesInJellyfin)} movies ({N(c.MoviesSearchable)} searchable), {N(c.EpisodesInJellyfin)} episodes ({N(c.EpisodesSearchable)} searchable)");
            if (c.EpisodeLists is { } e)
            {
                Line($"- Episode lists: {N(e.Resolved)} of {N(e.Seasons)} seasons; {N(e.Waiting)} waiting, {N(e.WaitingForShow)} of them for their show; running: {YesNo(e.Running)}");
            }
        }

        Heading("Downloads");
        var d = f.Downloads;
        Line($"- Last 24 hours, all accounts: {N(d.Last24hAllAccounts)}; limit per account: {N(d.LimitPerAccount)}");
        Line($"- Running now: {N(d.Active)}");
        if (d.BlockedUntil is { } blocked)
        {
            Line("- Paused until " + Time(blocked) + " (download history was damaged)");
        }

        Line($"- Cache: {(d.CacheBytes / 1048576.0).ToString("N0", CultureInfo.InvariantCulture)} MB; limit: {(d.CacheLimitGb > 0 ? N(d.CacheLimitGb) + " GB" : "none")}");

        Heading("Settings");
        var s = f.Settings;
        Line($"- Playback address: {Redactor.DescribeUrl(s.PublicUrl)} ({r.Scrub(s.PublicUrlSource, titles: false)})");
        Line($"- FlareSolverr configured: {YesNo(s.FlareSolverrConfigured)}");
        Line($"- AudioVault logins: {N(s.UsersWithAccount)} of {N(s.Users)} Jellyfin users");
        Item("Preferred language", s.PreferredLanguage);
        Line($"- Other languages: {YesNo(s.IncludeOtherLanguages)}; text-to-speech: {YesNo(s.IncludeTts)}; bitrate: {N(s.AudioBitrateKbps)} kbps");
        Line($"- Free space automatically: {YesNo(s.EvictWhenLowOnSpace)}; account menu link: {YesNo(s.ShowAccountMenuLink)}; sign out deleted users: {YesNo(s.SignOutDeletedUsers)}");

        Heading("Tasks");
        foreach (var t in f.Tasks)
        {
            var state = t.State == "Running" && t.Progress is { } p ? $"running, {Math.Floor(p).ToString(CultureInfo.InvariantCulture)}%" : t.State.ToLowerInvariant();
            var last = t.LastStatus is null ? "never run" : $"last {t.LastStatus.ToLowerInvariant()}" + (t.LastEnd is { } end ? " " + Time(end) : string.Empty);
            Line($"- {r.Scrub(t.Name, titles: false)}: {state}; {last}" + (t.LastFailed ? " (the error is in the Jellyfin log)" : string.Empty));
        }

        Heading("Recent problems");
        if (f.RecentProblems.Count == 0)
        {
            Line("- None");
        }

        foreach (var problem in f.RecentProblems)
        {
            Line($"- {Time(problem.At)} {problem.Kind}: {(problem.Failure is { } failure ? Describe(failure) : "no details")}");
        }

        return sb.ToString();
    }

    /// <summary>The address and login steps name the address and the admin's email; the report says only what kind.</summary>
    private static string StepDetail(SetupStep step, DiagnosticFacts f) => step.Key switch
    {
        "address" => $"Playback address: {Redactor.DescribeUrl(f.Settings.PublicUrl)}." + (step.State == SetupState.Done ? string.Empty : " " + step.Detail),
        "account" => step.State switch
        {
            SetupState.Done => "The viewing administrator has a login.",
            SetupState.Problem => "The viewing administrator's stored login can't be read.",
            _ => "The viewing administrator has no login.",
        } + $" {N(f.Settings.UsersWithAccount)} of {N(f.Settings.Users)} users have one.",
        "catalog" when step.Detail.StartsWith(SetupRules.CatalogReadFailed, StringComparison.Ordinal) => SetupRules.CatalogReadFailed + "; the error is in the Jellyfin log.",
        _ => step.At is { } at ? step.Detail.Replace("{at}", Time(at), StringComparison.Ordinal) : step.Detail,
    };

    /// <summary>Only the category, the exception's class name and the HTTP status: nothing from the message.</summary>
    public static string Describe(FailureInfo failure)
    {
        var type = ClassName().IsMatch(failure.ExceptionType) ? failure.ExceptionType : "unknown";
        return Category(failure.Kind) + $" ({type}" + (failure.HttpStatus is { } status ? $", HTTP {status.ToString(CultureInfo.InvariantCulture)})" : ")");
    }

    private static string Category(FailureKind kind) => kind switch
    {
        FailureKind.DownloadFailed => "download failed",
        FailureKind.DownloadIncomplete => "download stalled or incomplete",
        FailureKind.SourceUnavailable => "AudioVault unavailable or refused",
        FailureKind.SourceBlocked => "blocked by Cloudflare",
        FailureKind.SourceLayoutChanged => "AudioVault page layout changed",
        FailureKind.NoAccount => "no AudioVault login available",
        FailureKind.Network => "network error",
        FailureKind.Timeout => "timed out",
        FailureKind.NoAudio => "download had no audio files",
        FailureKind.ArchiveRejected => "archive too large or has too many files",
        FailureKind.ArchiveEntryOutsideFolder => "archive entry outside its folder",
        FailureKind.ConversionFailed => "conversion failed",
        FailureKind.ConversionUnavailable => "conversion unavailable",
        FailureKind.Disk => "disk or connection read/write error",
        FailureKind.DiskFull => "disk full",
        FailureKind.FileAccess => "file access denied",
        FailureKind.CacheFolderConflict => "cache folder holds a folder the plugin didn't make",
        FailureKind.DownloadLimit => "daily download limit reached",
        FailureKind.NotFound => "title or episode not found",
        _ => "other error",
    };

    private static string State(SetupState s) => s switch
    {
        SetupState.Done => "done",
        SetupState.InProgress => "in progress",
        SetupState.Waiting => "waiting",
        _ => "problem",
    };

    private static string Time(DateTimeOffset t) => t.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    private static string YesNo(bool b) => b ? "yes" : "no";

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]{0,79}$", RegexOptions.CultureInvariant)]
    private static partial Regex ClassName();

    private static string N(int? n) => n is { } v ? v.ToString("N0", CultureInfo.InvariantCulture) : "unknown";

    private static string OsName(string? os) =>
        os is null ? "unknown" : new string([.. os.Where(c => char.IsLetterOrDigit(c) || c is ' ' or '.' or '-' or '_' or '#' or ':' or '(' or ')' or '/')]).Trim();
}
