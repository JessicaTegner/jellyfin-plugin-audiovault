namespace AudioVault.Core.Media;

public static class LiveFileReader
{
    /// <summary>
    /// Copies a file that another process is still appending to, until <paramref name="writerDone"/> completes.
    /// If the writer failed, its exception is rethrown after the bytes written so far, so a truncated file never
    /// looks like a finished one.
    /// </summary>
    public static async Task CopyAsync(string path, Task writerDone, Stream destination, CancellationToken ct)
    {
        await using var source = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1,
            useAsync: true);
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                await destination.FlushAsync(ct).ConfigureAwait(false);
                continue;
            }

            if (writerDone.IsCompleted)
            {
                // One more pass: the writer may have appended between our read and its completion.
                read = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (read == 0)
                {
                    await writerDone.ConfigureAwait(false);
                    return;
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                continue;
            }

            await Task.WhenAny(writerDone, Task.Delay(200, ct)).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }
    }
}
