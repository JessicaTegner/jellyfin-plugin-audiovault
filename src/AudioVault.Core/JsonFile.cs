using System.Text.Json;
using System.Text.Json.Serialization;

namespace AudioVault.Core;

/// <summary>Reports state files that could not be parsed and were moved aside.</summary>
public static class StateFileRecovery
{
    /// <summary>Called with the damaged file's path, where it was moved (null if it couldn't be), and the parse error.</summary>
    public static Action<string, string?, Exception>? Quarantined { get; set; }
}

/// <summary>How a state file read went. See <see cref="JsonFile.ReadContained{T}"/>.</summary>
internal enum StateFileStatus
{
    /// <summary>No file: start fresh.</summary>
    Missing,

    /// <summary>Parsed. The value can still be null (the file held <c>null</c>); callers validate it.</summary>
    Loaded,

    /// <summary>Not valid JSON; moved aside (see <see cref="StateFileRecovery"/>).</summary>
    Corrupt,

    /// <summary>The file exists but couldn't be read (permissions, I/O error); left in place.</summary>
    Unreadable,
}

internal static class JsonFile
{
    private const string CorruptSuffix = ".corrupt-";

    private const string CorruptTimeFormat = "yyyyMMddHHmmss";

    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    public static T? Read<T>(string path) => Read<T>(path, out _);

    /// <summary>
    /// Malformed JSON is moved aside to <c>{path}.corrupt-{timestamp}</c> and reads as missing
    /// (<paramref name="corrupt"/> is then true), so one damaged file can't stop the plugin from starting.
    /// </summary>
    public static T? Read<T>(string path, out bool corrupt) => Read<T>(path, out corrupt, out _, setAside: true);

    /// <param name="error">The parse error when <paramref name="corrupt"/>.</param>
    /// <param name="setAside">False leaves a malformed file in place (the caller copies it aside itself).</param>
    public static T? Read<T>(string path, out bool corrupt, out JsonException? error, bool setAside)
    {
        error = null;
        corrupt = false;
        if (!File.Exists(path))
        {
            return default;
        }

        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<T>(stream, Options);
        }
        catch (JsonException ex)
        {
            corrupt = true;
            error = ex;
            if (setAside)
            {
                SetAside(path, ex);
            }

            return default;
        }
    }

    /// <summary>Like <see cref="Read{T}(string, out bool)"/>, but never throws: read errors become <see cref="StateFileStatus.Unreadable"/>.</summary>
    public static T? ReadContained<T>(string path, out StateFileStatus status) => ReadContained<T>(path, out status, out _, setAside: true);

    /// <param name="setAside">False leaves a malformed file in place; <paramref name="error"/> then says what was wrong.</param>
    public static T? ReadContained<T>(string path, out StateFileStatus status, out JsonException? error, bool setAside)
    {
        error = null;
        try
        {
            if (!File.Exists(path))
            {
                // Something else in the file's place is not a fresh start.
                status = Directory.Exists(path) ? StateFileStatus.Unreadable : StateFileStatus.Missing;
                return default;
            }

            var value = Read<T>(path, out var corrupt, out error, setAside);
            status = corrupt ? StateFileStatus.Corrupt : StateFileStatus.Loaded;
            return value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            status = StateFileStatus.Unreadable;
            return default;
        }
    }

    /// <summary>Moves a damaged state file to <c>{path}.corrupt-{timestamp}</c> and reports it. Never throws.</summary>
    public static void SetAside(string path, Exception reason)
    {
        string? movedTo = CorruptCopyPath(path, DateTimeOffset.UtcNow);
        try
        {
            File.Move(path, movedTo, overwrite: true);
        }
        catch (Exception moveError) when (moveError is IOException or UnauthorizedAccessException)
        {
            movedTo = null;
        }

        try
        {
            StateFileRecovery.Quarantined?.Invoke(path, movedTo, reason);
        }
        catch (Exception)
        {
            // Only reporting; a failing logger mustn't turn recovery into a crash.
        }
    }

    /// <summary>
    /// Copies a damaged state file to <c>{path}.corrupt-{timestamp}</c>, leaving the original in place, and reports
    /// it. Returns the copy's path, or null if it couldn't be made. Never throws.
    /// </summary>
    public static string? CopyAside(string path, Exception reason, DateTimeOffset at)
    {
        string? copy = CorruptCopyPath(path, at);
        try
        {
            File.Copy(path, copy, overwrite: true);
        }
        catch (Exception copyError) when (copyError is IOException or UnauthorizedAccessException)
        {
            copy = null;
        }

        try
        {
            StateFileRecovery.Quarantined?.Invoke(path, copy, reason);
        }
        catch (Exception)
        {
            // Only reporting.
        }

        return copy;
    }

    public static string CorruptCopyPath(string path, DateTimeOffset at) =>
        $"{path}{CorruptSuffix}{at.UtcDateTime.ToString(CorruptTimeFormat, System.Globalization.CultureInfo.InvariantCulture)}";

    /// <summary>When a <see cref="SetAside"/> or <see cref="CopyAside"/> copy of <paramref name="path"/> was made, from its name.</summary>
    public static IEnumerable<(string Path, DateTimeOffset At)> CorruptCopies(string path)
    {
        var dir = Path.GetDirectoryName(path)!;
        var prefix = Path.GetFileName(path) + CorruptSuffix;
        IEnumerable<string> files;
        try
        {
            files = Directory.Exists(dir) ? Directory.GetFiles(dir, prefix + "*") : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var file in files)
        {
            if (DateTimeOffset.TryParseExact(
                    Path.GetFileName(file)[prefix.Length..],
                    CorruptTimeFormat,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal,
                    out var at))
            {
                yield return (file, at);
            }
        }
    }

    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        using (var stream = File.Create(tmp))
        {
            JsonSerializer.Serialize(stream, value, Options);
        }

        File.Move(tmp, path, overwrite: true);
    }
}
