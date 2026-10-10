using AudioVault.Core.Catalog;
using LibrarySyncService = AudioVault.Core.Library.LibrarySyncService;
using LibraryConfigurationException = AudioVault.Core.Library.LibraryConfigurationException;
using AudioVault.Core.Media;
using AudioVault.Core.Policy;
using AudioVault.Core.Sources;
using Jellyfin.Plugin.AudioVault.Services;
using Jellyfin.Plugin.AudioVault.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Common.Api;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AudioVault.Api;

[ApiController]
[Route("AudioVault/Admin")]
[Authorize(Policy = Policies.RequiresElevation)]
public sealed class AdminController(
    IContentSource source,
    CatalogStore catalog,
    DownloadQuota quota,
    MediaJobManager jobs,
    CacheStore cache,
    LibraryBridge library,
    LibrarySyncService librarySync,
    PluginSettings settings,
    ITaskManager taskManager,
    PublicUrlMonitor publicUrl,
    AccountStore accounts,
    PluginHealth pluginHealth,
    ILibraryManager libraryManager,
    RepairCounts repairCounts,
    PlaybackReach reach,
    SetupProgress setupProgress,
    CompatibilityCheck compatibility,
    IServerApplicationHost host,
    IApplicationPaths paths,
    IAuthorizationContext auth,
    IUserManager users,
    MaintenanceService maintenance,
    ILogger<AdminController> logger) : ControllerBase
{
    [HttpGet("Status")]
    public async Task<ActionResult<object>> Status([FromQuery] bool checkSource, CancellationToken ct)
    {
        var x = await SnapshotAsync(checkSource, ct).ConfigureAwait(false);
        var url = x.Url;
        return new
        {
            SourceOk = x.Health?.Ok,
            SourceMessage = x.Health?.Message,
            EpisodeLists = x.EpisodeLists,
            DownloadsLast24hAllAccounts = Safe(() => quota.UsedByAll, 0),
            DownloadsBlockedUntil = Safe(() => quota.BlockedUntil, null),
            DownloadLimit = Safe(() => jobs.DownloadLimit, 0),
            ActiveJobs = Safe<IReadOnlyCollection<string>>(() => jobs.ActiveJobs, []),
            Jobs = Safe<object[]>(
                () => [.. jobs.ActiveJobProgress().Select(j => new { j.Id, j.Title, Phase = j.Phase.ToString(), j.BytesReceived, j.TotalBytes, j.Percent })],
                []),
            RecentOutcomes = Safe<object[]>(
                () => [.. jobs.RecentOutcomes().Select(o => new { o.Sequence, o.Title, Kind = o.Kind.ToString(), o.Error, o.At })],
                []),
            Tasks = Safe(TaskStatuses, []),
            Indexing = x.Indexing,
            CacheBytes = cache.CachedTotalBytes,
            settings.MoviesPath,
            settings.ShowsPath,
            settings.CachePath,
            FolderProblem = Safe(FolderProblem, null),
            CacheProblem = Safe(() => cache.OwnershipProblem, null),
            CatalogProblem = Safe(CatalogProblem, null),
            AccountsProblem = accounts.Problem,
            QuotaProblem = Safe(() => quota.Problem, null),
            ConversionProblem = Safe(ConversionProblem, null),
            PluginProblem = pluginHealth.Problem,
            PublicServerUrl = url.Url,
            PublicServerUrlSource = url.SourceText,
            settings.HasExplicitPublicUrl,
            PublicUrlProblem = url.Problem,
            // Only relevant while nothing better than the local fallback is known.
            DetectionProblem = url.Source == PublicUrlSource.Fallback ? publicUrl.DetectionProblem : null,
            PublicUrlSelfCheck = x.ServerCheck,
            PublicUrlAdvice = x.Advice,
            Unreached = x.Unreached.Select(r => new { Text = SetupRules.UnreachedText(r), r.LastAt }).ToArray(),
            RewriteProblem = publicUrl.RewriteProblem,
            LibraryProblem = Safe(LibraryFilesProblem, null),
            LibrariesCreated = x.Libraries is { Movies: true, Shows: true },
            Setup = x.Steps is null
                ? null
                : new
                {
                    Complete = SetupRules.Complete(x.Steps),
                    // Setup is complete, but some Jellyfin internals don't bind and their features are limited.
                    Limited = x.Compatibility is { } c && CompatibilityRules.Issues(c).Count > 0,
                    Steps = x.Steps.Select(st => new { st.Key, st.Title, State = st.State.ToString(), st.Detail, Action = st.Action.ToString(), st.At }).ToArray(),
                },
        };
    }

    /// <summary>A plain-text report for bug reports: errors as categories, other text redacted.</summary>
    [HttpGet("Diagnostics")]
    public async Task<ActionResult<object>> Diagnostics([FromQuery] bool checkSource, CancellationToken ct)
    {
        Snapshot x;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            // The connection check can wait on AudioVault or FlareSolverr; the report shouldn't wait long for it.
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            x = await SnapshotAsync(checkSource, timeout.Token).ConfigureAwait(false);
        }

        var config = PluginSettings.Config;
        var all = Safe(() => users.GetUsers().ToList(), []);
        var counts = x.Indexing is { } i
            ? new DiagnosticCounts(
                x.CatalogEntries.Count,
                x.CatalogEntries.Count(e => e.Entry.Kind == MediaKind.Movie),
                x.CatalogEntries.Count(e => e.Entry.Kind == MediaKind.Show),
                Safe(() => catalog.HasFullListing, false),
                i.Movies,
                i.Episodes,
                i.MoviesInJellyfin,
                i.EpisodesInJellyfin,
                i.MoviesSearchable,
                i.EpisodesSearchable,
                x.EpisodeLists)
            : null;
        var facts = new DiagnosticFacts(
            DateTimeOffset.UtcNow,
            new DiagnosticServer(
                Plugin.Instance?.Version.ToString() ?? "unknown",
                compatibility.JellyfinVersion,
                System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
                System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                ContainerHint()),
            x.Steps,
            x.Compatibility is { } c ? CompatibilityRules.Issues(c) : [],
            pluginHealth.Problem,
            new[]
            {
                x.Url.Problem, x.Url.Source == PublicUrlSource.Fallback ? publicUrl.DetectionProblem : null, x.ServerCheck, publicUrl.RewriteProblem,
                Safe(FolderProblem, null), Safe(LibraryFilesProblem, null), Safe(() => cache.OwnershipProblem, null), Safe(CatalogProblem, null), accounts.Problem,
                Safe(() => quota.Problem, null), Safe(ConversionProblem, null),
            }.OfType<string>().ToList(),
            x.Health?.Ok,
            x.Health?.Message,
            x.Health?.Failure,
            counts,
            new DiagnosticDownloads(
                Safe(() => quota.UsedByAll, 0),
                Safe(() => jobs.DownloadLimit, 0),
                Safe(() => jobs.ActiveJobProgress().Count, 0),
                Safe(() => quota.BlockedUntil, null),
                Safe(() => cache.CachedTotalBytes, 0L),
                config.MaxCacheGb),
            new DiagnosticSettings(
                x.Url.Url,
                x.Url.SourceText,
                !string.IsNullOrWhiteSpace(config.FlareSolverrUrl),
                config.PreferredLanguage,
                config.IncludeOtherLanguages,
                config.IncludeTts,
                config.AudioBitrateKbps,
                config.EvictWhenLowOnSpace,
                config.ShowAccountMenuLink,
                config.SignOutDeletedUsers,
                all.Count(u => accounts.Describe(u.Id) is not null),
                all.Count),
            x.Unreached.Count,
            Safe(DiagnosticTasks, []),
            Safe<IReadOnlyList<DiagnosticProblem>>(
                () => [.. jobs.RecentOutcomes().Where(o => o.Kind == JobOutcomeKind.Failed).Select(o => new DiagnosticProblem(o.At, "Download or conversion failed", o.Failure))],
                []));
        return new { Text = DiagnosticReport.Build(facts, new Redactor(Secrets(x, all), Titles(x))) };
    }

    /// <summary>Everything the status and the diagnostic report are built from, each part read on its own.</summary>
    private async Task<Snapshot> SnapshotAsync(bool checkSource, CancellationToken ct)
    {
        var health = checkSource ? await source.CheckHealthAsync(null, ct).ConfigureAwait(false) : null;
        publicUrl.Check();
        var url = settings.PublicUrl;
        string? selfCheck = null;
        if (checkSource && await publicUrl.ProbeAsync(url.Url, ct).ConfigureAwait(false) is { } problem)
        {
            // The server's own probes fetch the playback links too, so it must be able to reach the public URL.
            selfCheck = $"This server can't use its own public URL {url.Url}: {problem}";
        }

        // Each part on its own, so one failing (say, a folder being deleted while counted) doesn't hide the rest.
        var catalogEntries = Safe<IReadOnlyList<CatalogEntry>>(catalog.All, []);
        var indexing = Safe(IndexingStatus, null);
        var episodeLists = Safe<EpisodeListStatus?>(library.EpisodeLists, null);
        var unreached = Safe(() => reach.Reports(url.Url), []);
        var advice = await AddressAdviceAsync(url.Url, ct).ConfigureAwait(false);
        var serverCheck = url.Problem is null && selfCheck is null && Safe(() => publicUrl.CachedSelfCheck(url.Url), null) is { } cachedProblem
            ? $"This server can't use its own public URL {url.Url}: {cachedProblem}"
            : selfCheck;
        var (userId, deviceId) = await CallerAsync().ConfigureAwait(false);
        var libraries = Safe<LibraryFacts?>(Libraries, null);

        // Inside this request Jellyfin has run the plugin's filter and middleware, if it runs them at all.
        var compatibilityFacts = Safe<CompatibilityFacts?>(() => compatibility.Facts(inRequest: true), null);
        var steps = libraries is null
            ? null
            : Safe<IReadOnlyList<SetupStep>?>(
                () => SetupRules.Build(SetupFacts(url, serverCheck, advice, unreached, libraries, catalogEntries, indexing, episodeLists, userId, deviceId) with
                {
                    Compatibility = compatibilityFacts,
                }),
                null);
        return new Snapshot(health, url, serverCheck, advice, catalogEntries, indexing, episodeLists, unreached, libraries, steps, compatibilityFacts);
    }

    private sealed record Snapshot(
        SourceHealth? Health,
        PublicUrlChoice Url,
        string? ServerCheck,
        string? Advice,
        IReadOnlyList<CatalogEntry> CatalogEntries,
        IndexingCounts? Indexing,
        EpisodeListStatus? EpisodeLists,
        IReadOnlyList<UnreachedReport> Unreached,
        LibraryFacts? Libraries,
        IReadOnlyList<SetupStep>? Steps,
        CompatibilityFacts? Compatibility);

    private string? CatalogProblem() =>
        catalog.Unreadable
            ? "The AudioVault catalog is incomplete (its file was damaged or lost), so library updates are paused until the next full catalog sync. Use \"Run full catalog sync now\"."
            : null;

    private string? ConversionProblem() =>
        jobs.ConversionProblem is { } conversion ? "Titles can't be converted, so nothing new can be played: " + conversion : null;

    /// <summary>Values the diagnostic report must not show, whatever message they turn up in.</summary>
    private List<KnownSecret> Secrets(Snapshot x, IReadOnlyList<Jellyfin.Database.Implementations.Entities.User> all)
    {
        var config = PluginSettings.Config;
        var secrets = new List<KnownSecret>
        {
            new(x.Url.Url, "server address"),
            new(config.PublicServerUrl, "server address"),
            new(config.DetectedServerUrl, "server address"),
            new(config.FlareSolverrUrl, "FlareSolverr address"),
            new(settings.MoviesPath, "movies folder", true),
            new(settings.ShowsPath, "shows folder", true),
            new(settings.CachePath, "cache folder", true),
            new(settings.DataDirectory, "plugin data folder", true),
            new(Safe(() => host.FriendlyName, null), "server name"),
            new(Environment.MachineName, "server name"),
        };
        secrets.AddRange(Safe(() => settings.KnownAddresses, []).Select(a => new KnownSecret(a, "server address")));
        secrets.AddRange(new[]
        {
            paths.ProgramDataPath, paths.DataPath, paths.CachePath, paths.LogDirectoryPath, paths.ConfigurationDirectoryPath,
            paths.WebPath, paths.ProgramSystemPath, paths.TempDirectory, paths.PluginsPath,
        }.Select(p => new KnownSecret(p, "Jellyfin folder", true)));
        foreach (var user in all)
        {
            secrets.Add(new(user.Username, "user name"));
            secrets.Add(new(Safe(() => accounts.Describe(user.Id)?.Login, null), "AudioVault login"));
        }

        foreach (var report in x.Unreached)
        {
            secrets.Add(new(report.DeviceName, "device name"));
            secrets.Add(new(report.UserName, "user name"));
        }

        return secrets;
    }

    /// <summary>Every catalog title, and the titles of running and recent jobs (which name the season or episode too).</summary>
    private List<string> Titles(Snapshot x) =>
    [
        .. x.CatalogEntries.SelectMany(e => new[] { e.Entry.Title, e.Entry.RawName }),
        .. Safe(() => jobs.ActiveJobProgress().Select(j => j.Title).ToList(), []),
        .. Safe(() => jobs.RecentOutcomes().Select(o => o.Title).ToList(), []),
    ];

    private List<DiagnosticTask> DiagnosticTasks() =>
        [.. AudioVaultTasks().Select(t => new DiagnosticTask(
            t.Name,
            t.State.ToString(),
            t.State == TaskState.Running ? t.CurrentProgress : null,
            t.LastExecutionResult?.Status.ToString(),
            t.LastExecutionResult is { } r ? new DateTimeOffset(r.EndTimeUtc, TimeSpan.Zero) : null,
            t.LastExecutionResult?.Status == TaskCompletionStatus.Failed))];

    /// <summary>Hints that Jellyfin runs in a container; none of them identify the machine.</summary>
    private static string? ContainerHint()
    {
        var hints = new List<string>();
        if (string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase))
        {
            hints.Add(".NET container image");
        }

        if (System.IO.File.Exists("/.dockerenv"))
        {
            hints.Add("Docker");
        }

        if (System.IO.File.Exists("/run/.containerenv"))
        {
            hints.Add("Podman");
        }

        if (Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_HOST") is not null)
        {
            hints.Add("Kubernetes");
        }

        return hints.Count == 0 ? null : string.Join(", ", hints);
    }

    /// <summary>Starts a check of whether the admin's browser reaches this server at the playback address.</summary>
    [HttpPost("ReachProbe")]
    public async Task<ActionResult<object>> StartReachProbe()
    {
        var url = settings.PublicServerUrl;
        var nonce = reach.Probes.Create(url, await BrowserAsync().ConfigureAwait(false), DateTimeOffset.UtcNow);
        return new { Url = url, Nonce = nonce, PingUrl = $"{url}/AudioVault/Ping?n={nonce}" };
    }

    /// <summary>The browser is done: whether the ping arrived decides the result.</summary>
    [HttpPost("ReachProbe/{nonce}")]
    public async Task<ActionResult<object>> FinishReachProbe([FromRoute] string nonce, [FromQuery] bool loaded, [FromQuery] bool mixed)
    {
        var result = reach.Probes.Finish(nonce, await BrowserAsync().ConfigureAwait(false), loaded, mixed, DateTimeOffset.UtcNow);
        return result is null ? NotFound() : new { Outcome = result.Outcome.ToString(), result.Url, Text = SetupRules.BrowserText(result) };
    }

    /// <summary>The browser (user and device) this request came from; a browser check's result belongs to it alone.</summary>
    private async Task<string> BrowserAsync()
    {
        var caller = await CallerAsync().ConfigureAwait(false);
        return ReachProbes.Owner(caller.UserId, caller.DeviceId);
    }

    private async Task<(Guid UserId, string? DeviceId)> CallerAsync()
    {
        try
        {
            var info = await auth.GetAuthorizationInfo(HttpContext).ConfigureAwait(false);
            return (info.UserId, info.DeviceId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not tell which admin opened the settings page");
            return (Guid.Empty, null);
        }
    }

    private async Task<string?> AddressAdviceAsync(string url, CancellationToken ct)
    {
        try
        {
            var tailscale = await publicUrl.TailscaleAlternativeAsync(url, ct).ConfigureAwait(false);
            return PublicUrlRules.Advice(url, users.GetUsers().Count() > 1, [tailscale, .. settings.KnownAddresses]);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not check the shape of the public URL");
            return null;
        }
    }

    private SetupFacts SetupFacts(
        PublicUrlChoice url,
        string? serverCheck,
        string? advice,
        IReadOnlyList<UnreachedReport> unreached,
        LibraryFacts libraries,
        IReadOnlyList<CatalogEntry> entries,
        IndexingCounts? indexing,
        EpisodeListStatus? episodeLists,
        Guid userId,
        string? deviceId)
    {
        var workers = setupProgress.Sample();
        IScheduledTaskWorker? Worker(string key) => workers.FirstOrDefault(w => w.ScheduledTask.Key == key);
        static bool Running(IScheduledTaskWorker? w) => w?.State == TaskState.Running;

        var addressProblems = new[] { url.Problem, url.Source == PublicUrlSource.Fallback ? publicUrl.DetectionProblem : null, publicUrl.RewriteProblem, serverCheck }
            .Where(p => p is not null);
        var address = new AddressFacts(
            url.Url,
            addressProblems.Any() ? string.Join(' ', addressProblems) : null,
            publicUrl.RewritePending,
            advice,
            unreached.FirstOrDefault(),
            reach.Probes.LastFor(ReachProbes.Owner(userId, deviceId)));

        var reads = new[] { Worker(SetupProgress.FullSyncKey), Worker(SetupProgress.RecentSyncKey) }.OfType<IScheduledTaskWorker>().ToList();
        var reading = reads.FirstOrDefault(Running);
        var lastRead = reads
            .Select(w => w.LastExecutionResult)
            .Where(r => r?.Status == TaskCompletionStatus.Completed)
            .Max(r => (DateTimeOffset?)new DateTimeOffset(r!.EndTimeUtc, TimeSpan.Zero));
        var latest = reads.Select(w => w.LastExecutionResult).OfType<TaskResult>().MaxBy(r => r.EndTimeUtc);
        var catalogFacts = new CatalogFacts(
            entries.Count,
            entries.Count(e => e.Entry.Kind == MediaKind.Movie),
            entries.Count(e => e.Entry.Kind == MediaKind.Show),
            catalog.HasFullListing,
            catalog.Unreadable ? "The catalog file was damaged or lost, so it is incomplete until the next full catalog sync." : null,
            reading is not null,
            reading?.CurrentProgress,
            reading is null ? null : setupProgress.Remaining(reading.ScheduledTask.Key),
            lastRead,
            latest?.Status == TaskCompletionStatus.Failed ? latest.ErrorMessage ?? "see the Jellyfin log." : null);

        var scan = Worker(RepairRules.LibraryScanKey);
        var matching = new MatchingFacts(
            indexing?.Movies ?? 0,
            indexing?.MoviesInJellyfin,
            indexing?.MoviesSearchable,
            indexing?.Episodes ?? 0,
            indexing?.EpisodesInJellyfin,
            indexing?.EpisodesSearchable,
            Running(scan),
            Running(scan) ? scan!.CurrentProgress : null,
            Running(scan) ? setupProgress.Remaining(RepairRules.LibraryScanKey) : null,
            indexing?.ShowFoldersWaiting);

        var all = users.GetUsers().ToList();
        var mine = accounts.Get(userId);
        var account = new AccountFacts(
            mine is not null,
            mine is null && accounts.Describe(userId) is not null,
            mine?.Login,
            all.Count(u => accounts.Describe(u.Id) is not null),
            all.Count);

        return new SetupFacts(address, libraries, catalogFacts, matching, episodeLists, account);
    }

    private LibraryFacts Libraries()
    {
        var locations = libraryManager.GetVirtualFolders().SelectMany(f => f.Locations ?? []).ToHashSet(StringComparer.Ordinal);
        var problems = new[] { FolderProblem(), LibraryFilesProblem() }.OfType<string>().ToList();
        return new LibraryFacts(locations.Contains(settings.MoviesPath), locations.Contains(settings.ShowsPath), problems.Count == 0 ? null : string.Join(' ', problems));
    }

    /// <summary>Library files a settings change or the last library update couldn't bring up to date.</summary>
    private string? LibraryFilesProblem()
    {
        var problems = new[] { maintenance.VersionRewriteProblem, library.LibraryProblem }.OfType<string>().ToList();
        return problems.Count == 0 ? null : string.Join(' ', problems);
    }

    private T Safe<T>(Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Part of the AudioVault status could not be read");
            return fallback;
        }
    }

    /// <summary>Folders entered at the last save that weren't taken, and configured folders that can't be used.</summary>
    private string? FolderProblem()
    {
        var problems = new List<string>();
        if (Plugin.Instance?.FolderProblem is { } saved)
        {
            problems.Add(saved);
        }

        var folders = settings.Folders;
        if (folders.Problems.Count > 0)
        {
            problems.Add(string.Join(' ', folders.Problems) + " The default folder is used instead.");
        }

        try
        {
            LibrarySyncService.ValidateFolders(settings.Library());
        }
        catch (LibraryConfigurationException ex)
        {
            problems.Add(ex.Message);
        }

        return problems.Count == 0 ? null : string.Join(' ', problems.Distinct());
    }

    private IEnumerable<IScheduledTaskWorker> AudioVaultTasks() =>
        taskManager.ScheduledTasks
            .Where(t => t.Category == "AudioVault" || t.ScheduledTask.Key == RepairRules.LibraryScanKey)
            .OrderBy(t => t.Category == "AudioVault" ? 0 : 1);

    private object[] TaskStatuses() =>
        [.. AudioVaultTasks()
            .Select(t => new
            {
                t.Id,
                t.Name,
                State = t.State.ToString(),
                Progress = t.State == TaskState.Running ? t.CurrentProgress : null,
                LastStatus = t.LastExecutionResult?.Status.ToString(),
                LastEnd = t.LastExecutionResult?.EndTimeUtc,
                LastError = t.LastExecutionResult?.ErrorMessage,
            })];

    /// <summary>
    /// .strm files we wrote vs. items Jellyfin has in the library holding that folder (a COUNT query, no items loaded),
    /// and how many of those Jellyfin hasn't fetched details for yet, so they can't be found (counted by the repair
    /// pass; null until its first pass).
    /// </summary>
    private IndexingCounts IndexingStatus()
    {
        var folders = libraryManager.GetVirtualFolders();

        int? CountIn(string path, BaseItemKind kind)
        {
            var folder = folders.FirstOrDefault(f => (f.Locations ?? []).Contains(path, StringComparer.Ordinal));
            return folder is null || !Guid.TryParse(folder.ItemId, out var id)
                ? null
                : libraryManager.GetCount(new InternalItemsQuery
                {
                    IncludeItemTypes = [kind],
                    AncestorIds = [id],
                    Recursive = true,
                    IsVirtualItem = false,
                });
        }

        static int? Searchable(int? inJellyfin, int? waiting) =>
            inJellyfin is { } all && waiting is { } w ? Math.Max(0, all - w) : null;

        var (movies, episodes) = librarySync.IndexedByRoot;
        var moviesInJellyfin = CountIn(settings.MoviesPath, BaseItemKind.Movie);
        var episodesInJellyfin = CountIn(settings.ShowsPath, BaseItemKind.Episode);
        var moviesWaiting = repairCounts.Waiting(RepairKind.Movie);
        var episodesWaiting = repairCounts.Waiting(RepairKind.Episode);
        return new IndexingCounts(
            movies,
            moviesInJellyfin,
            Searchable(moviesInJellyfin, moviesWaiting),
            moviesWaiting,
            episodes,
            episodesInJellyfin,
            Searchable(episodesInJellyfin, episodesWaiting),
            episodesWaiting,
            repairCounts.Waiting(RepairKind.ShowFolder));
    }

    /// <summary>The settings page reports the address it was opened with; used when no public URL is configured.</summary>
    [HttpPost("DetectedAddress")]
    public async Task<ActionResult<object>> DetectedAddress([FromBody] DetectedAddressRequest request, CancellationToken ct)
    {
        var before = settings.PublicServerUrl;
        await publicUrl.AcceptDetectedAsync(request.Url, ct).ConfigureAwait(false);
        var after = settings.PublicUrl;
        return new { PublicServerUrl = after.Url, PublicServerUrlSource = after.SourceText, Changed = before != after.Url };
    }

    [HttpPost("Sync")]
    public ActionResult Sync([FromQuery] bool full)
    {
        if (full)
        {
            taskManager.QueueScheduledTask<FullCatalogSyncTask>();
        }
        else
        {
            taskManager.QueueScheduledTask<RecentCatalogSyncTask>();
        }

        return Accepted();
    }

    /// <summary>Creates Movies/Shows libraries pointing at the .strm folders, with video-only extras turned off.</summary>
    [HttpPost("CreateLibraries")]
    public async Task<ActionResult<object>> CreateLibraries([FromBody] NewLibraryOptions? choices = null)
    {
        choices ??= new NewLibraryOptions();
        var existing = libraryManager.GetVirtualFolders()
            .SelectMany(f => f.Locations ?? [])
            .ToHashSet(StringComparer.Ordinal);
        var created = new List<string>();
        foreach (var (name, type, path) in new[]
                 {
                     ("AudioVault Movies", CollectionTypeOptions.movies, settings.MoviesPath),
                     ("AudioVault Shows", CollectionTypeOptions.tvshows, settings.ShowsPath),
                 })
        {
            if (existing.Contains(path))
            {
                continue;
            }

            Directory.CreateDirectory(path);
            var options = new LibraryOptions
            {
                PathInfos = [new MediaPathInfo(path)],
                EnableTrickplayImageExtraction = false,
                ExtractTrickplayImagesDuringLibraryScan = false,
                EnableChapterImageExtraction = false,
                ExtractChapterImagesDuringLibraryScan = false,
                EnableRealtimeMonitor = false,
                TypeOptions = NewLibraryPlan.For(type == CollectionTypeOptions.movies, choices)
                    .Select(p => new TypeOptions
                    {
                        Type = p.Type,
                        MetadataFetchers = p.MetadataFetchers,
                        MetadataFetcherOrder = p.MetadataFetchers,
                        ImageFetchers = p.ImageFetchers,
                        ImageFetcherOrder = p.ImageFetchers,
                        ImageOptions = p.DisabledImageTypes
                            .Select(t => new ImageOption { Type = Enum.Parse<ImageType>(t), Limit = 0 })
                            .ToArray(),
                    })
                    .ToArray(),
            };
            await libraryManager.AddVirtualFolder(name, type, options, refreshLibrary: true).ConfigureAwait(false);
            created.Add(name);
        }

        return new { Created = created };
    }
}

public sealed record IndexingCounts(
    int Movies,
    int? MoviesInJellyfin,
    int? MoviesSearchable,
    int? MoviesWaiting,
    int Episodes,
    int? EpisodesInJellyfin,
    int? EpisodesSearchable,
    int? EpisodesWaiting,
    int? ShowFoldersWaiting);

public sealed class DetectedAddressRequest
{
    public string? Url { get; set; }
}
