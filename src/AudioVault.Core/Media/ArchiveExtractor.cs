using System.IO.Compression;
using AudioVault.Core.Sources;

namespace AudioVault.Core.Media;

/// <summary>Extracts the audio files of a downloaded season archive, within size and entry-count limits.</summary>
public static class ArchiveExtractor
{
    public const int DefaultMaxEntries = 1000;

    public const long DefaultMaxBytes = 20L * 1024 * 1024 * 1024;

    /// <summary>
    /// Writes into <paramref name="destination"/> (replaced), going through a temporary folder so the destination
    /// only ever holds a complete extraction. <paramref name="ensureSpace"/> gets the total uncompressed size first.
    /// </summary>
    public static async Task ExtractAudioAsync(
        string zipPath,
        string destination,
        int maxEntries,
        long maxBytes,
        Action<long> ensureSpace,
        CancellationToken ct)
    {
        var tmp = destination + ".tmp";
        CacheStore.TryDeleteDirectory(tmp);
        Directory.CreateDirectory(tmp);
        var root = Path.GetFullPath(tmp) + Path.DirectorySeparatorChar;
        try
        {
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                if (zip.Entries.Count > maxEntries)
                {
                    throw new SourceException($"Archive has {zip.Entries.Count} files; at most {maxEntries} are accepted") { Kind = FailureKind.ArchiveRejected };
                }

                var audio = zip.Entries
                    .Where(e => !string.IsNullOrEmpty(e.Name) && EpisodeMapper.IsAudio(e.Name) && !e.Name.StartsWith("._", StringComparison.Ordinal))
                    .ToList();
                var declared = audio.Sum(e => e.Length);
                if (declared > maxBytes)
                {
                    throw new SourceException($"Archive expands to {declared} bytes; at most {maxBytes} are accepted") { Kind = FailureKind.ArchiveRejected };
                }

                ensureSpace(declared);
                var budget = maxBytes;
                foreach (var entry in audio)
                {
                    var target = Path.GetFullPath(Path.Combine(tmp, entry.FullName));
                    if (!target.StartsWith(root, StringComparison.Ordinal))
                    {
                        throw new SourceException($"Archive entry {entry.FullName} points outside its folder") { Kind = FailureKind.ArchiveEntryOutsideFolder };
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    var input = await entry.OpenAsync(ct).ConfigureAwait(false);
                    await using (input.ConfigureAwait(false))
                    {
                        var output = File.Create(target);
                        await using (output.ConfigureAwait(false))
                        {
                            budget -= await CopyLimitedAsync(input, output, Math.Min(entry.Length, budget), ct).ConfigureAwait(false);
                        }
                    }
                }
            }

            CacheStore.TryDeleteDirectory(destination);
            Directory.Move(tmp, destination);
        }
        catch
        {
            CacheStore.TryDeleteDirectory(tmp);
            throw;
        }
    }

    /// <summary>Copies at most <paramref name="limit"/> bytes; more means the archive lied about its sizes.</summary>
    private static async Task<long> CopyLimitedAsync(Stream input, Stream output, long limit, CancellationToken ct)
    {
        var buffer = new byte[128 * 1024];
        long copied = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            copied += read;
            if (copied > limit)
            {
                throw new SourceException("Archive entry is larger than it claims");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }

        return copied;
    }
}
