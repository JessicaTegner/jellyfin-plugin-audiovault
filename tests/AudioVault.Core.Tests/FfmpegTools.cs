namespace AudioVault.Core.Tests;

/// <summary>
/// Finds ffmpeg/ffprobe for tests that run them. Without them those tests are reported as skipped, or fail when
/// the environment variable AUDIOVAULT_REQUIRE_FFMPEG=1 is set (use that for release builds).
/// </summary>
internal static class FfmpegTools
{
    public const string RequireVariable = "AUDIOVAULT_REQUIRE_FFMPEG";

    private static readonly string? FoundFfmpeg = Find("ffmpeg");
    private static readonly string? FoundFfprobe = Find("ffprobe");

    public static bool Available => FoundFfmpeg is not null && FoundFfprobe is not null;

    public static bool Required => Environment.GetEnvironmentVariable(RequireVariable) == "1";

    public static string? SkipReason =>
        Available || Required ? null : $"ffmpeg/ffprobe not found (set {RequireVariable}=1 to fail instead of skipping)";

    public static string Ffmpeg => FoundFfmpeg ?? throw Missing("ffmpeg");

    public static string Ffprobe => FoundFfprobe ?? throw Missing("ffprobe");

    public static string? Find(string name)
    {
        var names = OperatingSystem.IsWindows() ? new[] { name + ".exe", name } : [name];
        return (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Concat(OperatingSystem.IsWindows() ? [] : ["/opt/homebrew/bin", "/usr/local/bin", "/usr/bin"])
            .SelectMany(dir => names.Select(n => Path.Combine(dir.Trim('"'), n)))
            .FirstOrDefault(File.Exists);
    }

    private static InvalidOperationException Missing(string name) =>
        new($"{name} was not found on PATH but {RequireVariable}=1 requires it");
}

/// <summary>A test that runs ffmpeg; skipped when it isn't installed (see <see cref="FfmpegTools"/>).</summary>
public sealed class FfmpegFactAttribute : FactAttribute
{
    public FfmpegFactAttribute() => Skip = FfmpegTools.SkipReason;
}

/// <summary>A theory that runs ffmpeg; skipped when it isn't installed (see <see cref="FfmpegTools"/>).</summary>
public sealed class FfmpegTheoryAttribute : TheoryAttribute
{
    public FfmpegTheoryAttribute() => Skip = FfmpegTools.SkipReason;
}
