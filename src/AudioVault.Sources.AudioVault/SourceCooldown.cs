using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AudioVault.Sources.AudioVault;

/// <summary>
/// Shared by every session: AudioVault rate limits and Cloudflare blocks apply to this server's IP, not to one account,
/// so after one the whole source stops sending until the cooldown ends. Kept in the state directory, when there is one,
/// so a restart doesn't end a pause early.
/// </summary>
internal sealed class SourceCooldown(TimeProvider? time = null, Func<string?>? stateDirectory = null, ILogger? logger = null)
{
    public static readonly TimeSpan Default = TimeSpan.FromMinutes(15);

    public const string FileName = "audiovault-cooldown.json";

    private static readonly TimeSpan Min = TimeSpan.FromMinutes(1);

    // Only for pauses chosen here; AudioVault's own Retry-After is honored however long it is.
    private static readonly TimeSpan MaxLocal = TimeSpan.FromHours(24);

    // Keeps a nonsensical Retry-After from overflowing the deadline.
    private static readonly TimeSpan MaxProvider = TimeSpan.FromDays(365);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _lock = new();
    private State? _state;
    private string? _loadedFrom;

    /// <summary>The reason to hold off, or null when requests may be sent.</summary>
    /// <param name="flareSolverrUrl">A Cloudflare block ends early once a different FlareSolverr is configured.</param>
    public string? Active(string baseUrl, string? flareSolverrUrl)
    {
        lock (_lock)
        {
            LoadIfNeeded();
            if (_state is not { } state || _time.GetUtcNow() >= state.Until || state.BaseUrl != baseUrl
                || (state.FlareSolverrUrl is { } blockedWith && blockedWith != (flareSolverrUrl ?? string.Empty)))
            {
                return null;
            }

            var until = state.Until.ToLocalTime();
            var format = state.Until - _time.GetUtcNow() > TimeSpan.FromHours(20) ? "yyyy-MM-dd HH:mm" : "HH:mm";
            return $"{state.Message} AudioVault is paused until {until.ToString(format, System.Globalization.CultureInfo.InvariantCulture)}.";
        }
    }

    /// <summary>Starts or extends the cooldown; never shortens one already running.</summary>
    /// <param name="fromProvider">The duration is AudioVault's own Retry-After, which is not capped at a day.</param>
    public void Trip(TimeSpan duration, string message, string baseUrl, string? blockedWithFlareSolverr = null, bool fromProvider = false)
    {
        var max = fromProvider ? MaxProvider : MaxLocal;
        duration = duration < Min ? Min : duration > max ? max : duration;
        lock (_lock)
        {
            LoadIfNeeded();
            var until = _time.GetUtcNow() + duration;
            if (_state is { } current && current.BaseUrl == baseUrl && current.Until >= until && _time.GetUtcNow() < current.Until)
            {
                return;
            }

            _state = new State(until, message, baseUrl, blockedWithFlareSolverr);
            Save(_state);
        }
    }

    private string? FilePath() =>
        stateDirectory?.Invoke() is { Length: > 0 } directory ? Path.Combine(directory, FileName) : null;

    /// <summary>Reads the saved pause once per state file; a missing or unreadable file just means none.</summary>
    private void LoadIfNeeded()
    {
        var path = FilePath();
        if (path is null || path == _loadedFrom)
        {
            return;
        }

        _loadedFrom = path;
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            if (JsonSerializer.Deserialize<State>(File.ReadAllText(path)) is { BaseUrl: { Length: > 0 }, Message: not null } saved
                && (_state is null || saved.Until > _state.Until))
            {
                _state = saved;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            logger?.LogWarning(ex, "Ignoring unreadable AudioVault pause state in {Path}", path);
        }
    }

    private void Save(State state)
    {
        var path = FilePath();
        if (path is null)
        {
            return;
        }

        var temp = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temp, JsonSerializer.Serialize(state));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still paused in memory; only a restart would forget it.
            logger?.LogWarning(ex, "Could not save the AudioVault pause to {Path}", path);
        }
    }

    private sealed record State(DateTimeOffset Until, string Message, string BaseUrl, string? FlareSolverrUrl);
}
