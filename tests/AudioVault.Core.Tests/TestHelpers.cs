using AudioVault.Core.Sources;

namespace AudioVault.Core.Tests;

internal static class E
{
    public static SourceEntry Movie(string id, string title, int? year, string language = "English", bool tts = false, params string[] tags) =>
        new(id, MediaKind.Movie, title, year, null, null, new VersionInfo(language, tts, tags), title);

    public static SourceEntry Show(string id, string title, int season, int? year, int? part = null, string language = "English", bool tts = false) =>
        new(id, MediaKind.Show, title, year, season, part, new VersionInfo(language, tts, []), title);
}

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("avtest").FullName;

    public void Dispose() => Directory.Delete(Path, recursive: true);
}

internal sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;
}
