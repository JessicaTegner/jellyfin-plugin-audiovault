using System.Globalization;

namespace Jellyfin.Plugin.AudioVault.Services;

public enum SetupState
{
    Done,
    InProgress,
    Waiting,
    Problem,
}

/// <summary>The next step a checklist item offers; each one reuses a control the settings page already has.</summary>
public enum SetupAction
{
    None,
    PublicUrl,
    LibraryOptions,
    SyncRecent,
    SyncFull,
    AccountPage,
}

/// <param name="At">A time the detail mentions as "{at}"; the page writes it in the viewer's own time zone.</param>
public sealed record SetupStep(string Key, string Title, SetupState State, string Detail, SetupAction Action = SetupAction.None, DateTimeOffset? At = null);

public enum BrowserOutcome
{
    Reached,
    OtherServer,
    NotReached,
}

/// <summary>The last check, from an admin's browser, of whether it can load the playback address.</summary>
/// <param name="Mixed">The page was open over https and the address is http, which browsers block.</param>
public sealed record BrowserCheck(string Url, BrowserOutcome Outcome, bool Mixed, DateTimeOffset At);

/// <param name="Problem">Why the address can't work (invalid, local only, files not rewritten, unreachable from the server).</param>
public sealed record AddressFacts(string Url, string? Problem, bool Rewriting, string? Advice, UnreachedReport? Unreached, BrowserCheck? Browser);

public sealed record LibraryFacts(bool Movies, bool Shows, string? Problem);

/// <param name="Complete">The catalog came from a full listing.</param>
/// <param name="Progress">Percent of the running read, if one is running.</param>
/// <param name="LastError">Why the last read failed, if it did.</param>
public sealed record CatalogFacts(
    int Titles,
    int Movies,
    int ShowSeasons,
    bool Complete,
    string? Problem,
    bool Running,
    double? Progress,
    TimeSpan? Remaining,
    DateTimeOffset? LastRead,
    string? LastError);

/// <summary>Written files, items in Jellyfin and searchable items; null where Jellyfin has no library or nothing is counted yet.</summary>
public sealed record MatchingFacts(
    int Movies,
    int? MoviesInJellyfin,
    int? MoviesSearchable,
    int Episodes,
    int? EpisodesInJellyfin,
    int? EpisodesSearchable,
    bool Scanning,
    double? ScanProgress,
    TimeSpan? Remaining,
    int? ShowFoldersWaiting = null);

public sealed record AccountFacts(bool HasAccount, bool Unreadable, string? Email, int UsersWithAccount, int Users);

public sealed record SetupFacts(
    AddressFacts Address,
    LibraryFacts Libraries,
    CatalogFacts Catalog,
    MatchingFacts Matching,
    EpisodeListStatus? EpisodeLists,
    AccountFacts Account,
    CompatibilityFacts? Compatibility = null);

/// <summary>The first-time setup checklist on the settings page, from what the server knows. No Jellyfin types.</summary>
public static class SetupRules
{
    public const string CatalogReadFailed = "The last catalog read failed";

    private const string EpisodeOneNote = " Until then a season shows only episode 1.";

    /// <summary>Within this share of all titles, matching counts as done: new uploads keep a few waiting for the next scan.</summary>
    private const double MatchingSlack = 0.01;

    public static IReadOnlyList<SetupStep> Build(SetupFacts f)
    {
        var matching = Matching(f);
        IReadOnlyList<SetupStep> steps =
            [Address(f.Address), Libraries(f.Libraries), Catalog(f.Catalog), matching, EpisodeLists(f, matching.State == SetupState.Done), Account(f.Account)];
        return f.Compatibility is { } compatibility ? [.. steps, CompatibilityRules.Step(compatibility)] : steps;
    }

    public static bool Complete(IEnumerable<SetupStep> steps) => steps.All(s => s.State == SetupState.Done);

    public static SetupStep Address(AddressFacts a)
    {
        const string Key = "address", Title = "Playback address works";
        var problems = new List<string>();
        if (a.Problem is { } problem)
        {
            problems.Add(problem);
        }

        DateTimeOffset? at = null;
        if (a.Unreached is { } report)
        {
            problems.Add(UnreachedText(report) + $" Check that it can reach {a.Url}.");
            at = report.LastAt;
        }

        if (a.Browser is { Outcome: not BrowserOutcome.Reached } browser && SameUrl(browser.Url, a.Url))
        {
            problems.Add(BrowserText(browser));
        }

        if (problems.Count > 0)
        {
            return new SetupStep(Key, Title, SetupState.Problem, string.Join(' ', problems), SetupAction.PublicUrl, at);
        }

        if (a.Rewriting)
        {
            return new SetupStep(Key, Title, SetupState.InProgress, $"Updating the library files to use {a.Url}.");
        }

        var detail = a.Browser is { Outcome: BrowserOutcome.Reached } reached && SameUrl(reached.Url, a.Url)
            ? $"Playback links use {a.Url}, and this browser reached it."
            : $"Playback links use {a.Url}.";

        // Advice doesn't block: the address works for the devices that can resolve it.
        return new SetupStep(Key, Title, SetupState.Done, a.Advice is null ? detail : detail + " Note: " + a.Advice);
    }

    public static SetupStep Libraries(LibraryFacts l)
    {
        const string Key = "libraries", Title = "Libraries created";
        if (l.Problem is { } problem)
        {
            return new SetupStep(Key, Title, SetupState.Problem, problem);
        }

        if (l.Movies && l.Shows)
        {
            return new SetupStep(Key, Title, SetupState.Done, "Jellyfin has libraries for the movies and shows folders.");
        }

        var missing = !l.Movies && !l.Shows ? "the movies and shows folders" : l.Movies ? "the shows folder" : "the movies folder";
        return new SetupStep(
            Key,
            Title,
            SetupState.Waiting,
            $"No Jellyfin library uses {missing} yet. Choose the options below, then press Create AudioVault libraries in Jellyfin.",
            SetupAction.LibraryOptions);
    }

    public static SetupStep Catalog(CatalogFacts c)
    {
        const string Key = "catalog", Title = "Catalog synced";
        const string FirstRead = "The first read takes about 10 minutes.";
        if (c.Problem is { } problem)
        {
            return new SetupStep(Key, Title, SetupState.Problem, problem, SetupAction.SyncFull);
        }

        if (c.Complete && c.Titles > 0)
        {
            var counts = $"{N(c.Titles)} titles ({N(c.Movies)} movies, {N(c.ShowSeasons)} show seasons)";
            return c.LastRead is { } last
                ? new SetupStep(Key, Title, SetupState.Done, counts + ", last read {at}.", At: last)
                : new SetupStep(Key, Title, SetupState.Done, counts + ".");
        }

        if (c.Running)
        {
            return new SetupStep(Key, Title, SetupState.InProgress, "Reading the catalog" + Progress(c.Progress, c.Remaining) + ". " + FirstRead);
        }

        if (c.LastError is { } error)
        {
            return new SetupStep(Key, Title, SetupState.Problem, $"{CatalogReadFailed}: {error}", c.Titles == 0 ? SetupAction.SyncRecent : SetupAction.SyncFull);
        }

        return c.Titles == 0
            ? new SetupStep(Key, Title, SetupState.Waiting, "Not read yet; press Sync recent uploads now. " + FirstRead, SetupAction.SyncRecent)
            : new SetupStep(Key, Title, SetupState.Waiting, $"{N(c.Titles)} titles so far; the whole catalog hasn't been read yet. " + FirstRead, SetupAction.SyncFull);
    }

    public static SetupStep Matching(SetupFacts f)
    {
        const string Key = "matching", Title = "Jellyfin matching titles";
        var m = f.Matching;
        if (!(f.Libraries.Movies && f.Libraries.Shows))
        {
            return new SetupStep(Key, Title, SetupState.Waiting, "Starts once the libraries exist.");
        }

        var total = m.Movies + m.Episodes;
        if (total == 0)
        {
            // After a restart the counts arrive with the plugin's next library pass.
            return f.Catalog.Complete
                ? new SetupStep(Key, Title, SetupState.InProgress, "Counting the titles in Jellyfin, which takes up to 30 minutes after Jellyfin starts.")
                : new SetupStep(Key, Title, SetupState.Waiting, "Starts once the catalog has been read.");
        }

        var searchable = m.MoviesSearchable + m.EpisodesSearchable;
        if (searchable is { } s && total - s <= total * MatchingSlack)
        {
            return new SetupStep(
                Key,
                Title,
                SetupState.Done,
                s >= total
                    ? $"All {N(m.Movies)} movies and {N(m.Episodes)} episodes are searchable."
                    : $"{Of(m.MoviesSearchable, m.Movies)} movies and {Of(m.EpisodesSearchable, m.Episodes)} episodes searchable; the rest are new and wait for the next library scan.");
        }

        var added = $"{Of(m.MoviesInJellyfin, m.Movies)} movies and {Of(m.EpisodesInJellyfin, m.Episodes)} episodes added";
        var counts = searchable is null
            ? added + "; counting how many are searchable."
            : added + $"; {N(m.MoviesSearchable)} movies and {N(m.EpisodesSearchable)} episodes searchable"
                + (m.ShowFoldersWaiting is > 0 and var folders ? $", and {N(folders)} {(folders == 1 ? "show or season waits" : "shows or seasons wait")} for their details." : ".");
        var progress = string.Join(", ", new[]
        {
            m.ScanProgress is { } p ? Math.Floor(p).ToString(CultureInfo.InvariantCulture) + "%" : null,
            m.Remaining is { } r ? About(r) + " left" : null,
        }.OfType<string>());
        var expectation = m.Scanning
            ? " Library scan " + (progress.Length > 0 ? progress : "running") + ". Titles appear in search gradually; the first time takes hours."
            : " Titles appear in search gradually; the plugin asks Jellyfin to finish any an interrupted scan left waiting.";
        return new SetupStep(Key, Title, SetupState.InProgress, counts + expectation);
    }

    public static SetupStep EpisodeLists(SetupFacts f, bool matchingDone)
    {
        const string Key = "episodes", Title = "Episode lists";
        var e = f.EpisodeLists;
        if (!f.Libraries.Shows)
        {
            return new SetupStep(Key, Title, SetupState.Waiting, "Starts once the shows library exists." + EpisodeOneNote);
        }

        if (e is null || (e.Seasons == 0 && !f.Catalog.Complete))
        {
            return new SetupStep(Key, Title, SetupState.Waiting, "Starts once the catalog has been read." + EpisodeOneNote);
        }

        if (e.Seasons == 0 && f.Catalog.ShowSeasons > 0)
        {
            return new SetupStep(Key, Title, SetupState.InProgress, "Counting the seasons in the shows library, which takes a few minutes after Jellyfin starts." + EpisodeOneNote);
        }

        var counts = $"{N(e.Resolved)} of {N(e.Seasons)} seasons have their episode list";
        if (e.Waiting == 0)
        {
            return new SetupStep(Key, Title, SetupState.Done, counts + ".");
        }

        if (e.Running)
        {
            return new SetupStep(Key, Title, SetupState.InProgress, $"{counts}; looking up the other {N(e.Waiting)} now." + EpisodeOneNote);
        }

        if (e.Failed > 0)
        {
            var seasons = e.Failed == 1 ? "1 season" : $"{N(e.Failed)} seasons";
            return new SetupStep(
                Key,
                Title,
                SetupState.Problem,
                $"{counts}; looking up {seasons} failed, for example because TMDb couldn't be reached. "
                + $"They are tried again {Every(EpisodeListRules.RetryFailed)}; the Jellyfin log has details." + EpisodeOneNote);
        }

        if (e.WaitingForShow is not { } forShow)
        {
            return new SetupStep(Key, Title, SetupState.InProgress, $"{counts}; {N(e.Waiting)} are looked up automatically after library scans." + EpisodeOneNote);
        }

        var untried = Math.Max(0, e.Waiting - (e.Tried ?? 0) - forShow);
        if (untried > 0)
        {
            var later = forShow > 0 ? $" and {N(forShow)} once Jellyfin identifies their show" : string.Empty;
            return new SetupStep(Key, Title, SetupState.InProgress, $"{counts}; {N(untried)} are looked up automatically after library scans{later}." + EpisodeOneNote);
        }

        if (forShow > 0 && !matchingDone)
        {
            return new SetupStep(Key, Title, SetupState.Waiting, $"{counts}; {N(forShow)} wait for Jellyfin to identify their show." + EpisodeOneNote);
        }

        // Everything has been asked for: what's left has no list on TMDb (or its show can't be identified) for now.
        return new SetupStep(
            Key,
            Title,
            SetupState.Done,
            $"{counts}; the other {N(e.Waiting)} have no list on TMDb yet. They show only episode 1 and are checked again {Every(EpisodeListRules.RetryNothingFound)}.");
    }

    private static string Every(TimeSpan t) => t == TimeSpan.FromHours(1) ? "every hour" : $"every {t.TotalHours.ToString(CultureInfo.InvariantCulture)} hours";

    public static SetupStep Account(AccountFacts a)
    {
        const string Key = "account", Title = "Your AudioVault login";
        var others = a.Users > 1 ? $" {N(a.UsersWithAccount)} of {N(a.Users)} Jellyfin users have one." : string.Empty;
        if (a.Unreadable)
        {
            return new SetupStep(Key, Title, SetupState.Problem, "Your stored AudioVault login can't be read; clear it and add it again." + others, SetupAction.AccountPage);
        }

        return a.HasAccount
            ? new SetupStep(Key, Title, SetupState.Done, $"Signed in as {a.Email}." + others)
            : new SetupStep(
                Key,
                Title,
                SetupState.Waiting,
                "Add your AudioVault login to play titles nobody has played yet; other users add theirs on the same page." + others,
                SetupAction.AccountPage);
    }

    public static string UnreachedText(UnreachedReport r)
    {
        var who = r.UserName is null ? $"{r.DeviceName} ({r.Client})" : $"{r.DeviceName} ({r.Client}, {r.UserName})";
        var times = r.Count > 1 ? $", {N(r.Count)} times" : string.Empty;
        return $"A player on {who} couldn't load the playback address (last at {{at}}{times}).";
    }

    public static string BrowserText(BrowserCheck b) => b.Outcome switch
    {
        BrowserOutcome.Reached => $"This browser reached this server at {b.Url}.",
        BrowserOutcome.OtherServer => $"This browser got an answer at {b.Url}, but not from this Jellyfin server.",
        _ when b.Mixed => $"This browser couldn't load {b.Url}: this page is open over https and the address is http, which browsers block. Players in a browser opened over https can't use it either.",
        _ => $"This browser couldn't reach {b.Url}; this device may not resolve that name or connect to it.",
    };

    /// <summary>A rough remaining time, in words.</summary>
    public static string About(TimeSpan t)
    {
        if (t < TimeSpan.FromMinutes(1))
        {
            return "less than a minute";
        }

        if (t < TimeSpan.FromMinutes(90))
        {
            var minutes = t.TotalMinutes < 10 ? (int)Math.Ceiling(t.TotalMinutes) : (int)Math.Round(t.TotalMinutes / 5) * 5;
            return minutes == 1 ? "about 1 minute" : $"about {minutes} minutes";
        }

        return $"about {Math.Round(t.TotalHours).ToString(CultureInfo.InvariantCulture)} hours";
    }

    private static string Progress(double? percent, TimeSpan? remaining) =>
        (percent is { } p ? $": {Math.Floor(p).ToString(CultureInfo.InvariantCulture)}% done" : string.Empty)
        + (remaining is { } r ? $", {About(r)} left" : string.Empty);

    private static bool SameUrl(string a, string b) =>
        string.Equals(PublicUrlRules.Normalize(a) ?? a, PublicUrlRules.Normalize(b) ?? b, StringComparison.OrdinalIgnoreCase);

    private static string Of(int? part, int all) => part == all ? N(all) : $"{N(part)} of {N(all)}";

    private static string N(int? n) => n is { } v ? v.ToString("N0", CultureInfo.InvariantCulture) : "?";
}

/// <summary>
/// Remaining time from the rate actually observed over the last few minutes, per key (a task's progress, say). A value
/// lower than the last one starts a new run.
/// </summary>
public sealed class ProgressRate(TimeSpan window, TimeSpan minimumSpan)
{
    private const int MaxSamples = 500;
    private static readonly TimeSpan StallLimit = TimeSpan.FromMinutes(2);
    private readonly Dictionary<string, List<(DateTimeOffset At, double Value)>> _samples = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    public void Observe(string key, double value, DateTimeOffset now)
    {
        lock (_lock)
        {
            if (!_samples.TryGetValue(key, out var list))
            {
                _samples[key] = list = [];
            }

            if (list.Count > 0 && value < list[^1].Value)
            {
                list.Clear();
            }

            list.Add((now, value));
            list.RemoveAll(s => now - s.At > window);
            if (list.Count > MaxSamples)
            {
                list.RemoveRange(0, list.Count - MaxSamples);
            }
        }
    }

    /// <summary>Time until the value reaches <paramref name="target"/> at the observed rate; null without enough recent progress.</summary>
    public TimeSpan? Remaining(string key, double target, DateTimeOffset now)
    {
        lock (_lock)
        {
            if (!_samples.TryGetValue(key, out var list) || list.Count < 2)
            {
                return null;
            }

            var (firstAt, firstValue) = list[0];
            var (lastAt, lastValue) = list[^1];
            var span = lastAt - firstAt;
            if (span < minimumSpan || lastValue <= firstValue || now - lastAt > window)
            {
                return null;
            }

            // Jellyfin's scan races to about half, then crawls: no estimate once the value has stopped moving.
            var since = list.Find(x => x.Value == lastValue).At;
            if (now - since > StallLimit)
            {
                return null;
            }

            var perSecond = (lastValue - firstValue) / span.TotalSeconds;
            return TimeSpan.FromSeconds(Math.Max(0, target - lastValue) / perSecond);
        }
    }
}
