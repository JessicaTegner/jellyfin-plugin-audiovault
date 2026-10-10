using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace AudioVault.Core.Media;

public sealed record ConversionSettings(int AudioBitrateKbps = 96, int Width = 640, int Height = 360, int FrameRate = 24);

/// <summary>ffmpeg can't be run, so nothing would become playable; thrown before any download is reserved or opened.</summary>
public sealed class ConversionUnavailableException(string message) : Sources.SourceException(message);

public sealed class ConversionFailedException(string message) : InvalidOperationException(message);

/// <summary>Wraps audio in an MP4 with a black H.264 track so every Jellyfin client treats it as video.</summary>
public sealed class FfmpegConverter(Func<string> ffmpegPath, ILogger<FfmpegConverter> logger)
{
    private static readonly TimeSpan ReadyCheckValidFor = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan FailedCheckValidFor = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ReadyCheckTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(10);

    private readonly Lock _readinessLock = new();
    private (string Path, long At, string? Problem)? _readiness;

    /// <summary>Checks the ffmpeg at a path; returns why it can't convert, or null. Replaceable for tests.</summary>
    internal Func<string, string?> ReadinessProbe { get; set; } = Probe;

    /// <summary>
    /// Why conversions can't run (ffmpeg missing, not executable, or without the needed encoders), or null when
    /// they can. Doesn't touch AudioVault. A success is remembered for a few minutes, a failure only briefly.
    /// </summary>
    public string? ReadinessProblem()
    {
        string path;
        try
        {
            path = ffmpegPath();
        }
        catch (Exception ex)
        {
            return $"Jellyfin's ffmpeg path is unavailable ({ex.Message}).";
        }

        lock (_readinessLock)
        {
            var now = Environment.TickCount64;
            if (_readiness is { } known && known.Path == path
                && now - known.At < (known.Problem is null ? ReadyCheckValidFor : FailedCheckValidFor).TotalMilliseconds)
            {
                return known.Problem;
            }

            string? problem;
            try
            {
                problem = ReadinessProbe(path);
            }
            catch (Exception ex)
            {
                problem = $"ffmpeg at {path} could not be checked ({ex.Message}).";
            }

            if (problem is not null)
            {
                logger.LogWarning("AudioVault can't convert media: {Problem}", problem);
            }

            _readiness = (path, Environment.TickCount64, problem);
            return problem;
        }
    }

    /// <summary>Forgets the last readiness check, e.g. after ffmpeg failed to start.</summary>
    private void ForgetReadiness()
    {
        lock (_readinessLock)
        {
            _readiness = null;
        }
    }

    private static string? Probe(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "No ffmpeg is set in Jellyfin (Dashboard > Playback > Transcoding > FFmpeg path).";
        }

        if (!File.Exists(path))
        {
            return $"ffmpeg was not found at {path}. Check Jellyfin's FFmpeg path (Dashboard > Playback > Transcoding).";
        }

        var psi = new ProcessStartInfo(path)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-encoders");
        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return $"ffmpeg at {path} can't be started ({ex.Message}). Check that it is executable.";
        }

        if (process is null)
        {
            return $"ffmpeg at {path} can't be started.";
        }

        using (process)
        {
            var stdout = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(ReadyCheckTimeout))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }

                return $"ffmpeg at {path} did not respond.";
            }

            var encoders = stdout.Wait(ReadyCheckTimeout) ? stdout.Result : string.Empty;
            if (process.ExitCode != 0)
            {
                return $"ffmpeg at {path} failed to run (exit code {process.ExitCode}).";
            }

            var missing = new[] { "libx264", "aac" }
                .Where(e => !System.Text.RegularExpressions.Regex.IsMatch(encoders, $@"^\s*\S+\s+{e}\s", System.Text.RegularExpressions.RegexOptions.Multiline))
                .ToList();
            return missing.Count == 0 ? null : $"ffmpeg at {path} lacks the {string.Join(" and ", missing)} encoder needed to convert audio descriptions.";
        }
    }
    public static IReadOnlyList<string> BuildConvertArgs(string input, string output, string? title, ConversionSettings s)
    {
        var args = new List<string>
        {
            "-hide_banner", "-nostats", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", $"color=c=black:s={s.Width}x{s.Height}:r={s.FrameRate}",
            "-i", input,
            // MP3 cover art shows up as a video stream, so map explicitly.
            "-map", "0:v:0", "-map", "1:a:0", "-map_metadata", "-1",
            "-c:v", "libx264", "-preset", "ultrafast", "-tune", "stillimage", "-pix_fmt", "yuv420p",
            "-g", (s.FrameRate * 2).ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-c:a", "aac", "-b:a", $"{s.AudioBitrateKbps}k",
            "-shortest",
            "-disposition:a:0", "default+visual_impaired",
            "-metadata:s:a:0", "title=Audio Description",
        };
        if (!string.IsNullOrWhiteSpace(title))
        {
            args.AddRange(["-metadata", $"title={title}"]);
        }

        // Fragmented so the file can be read while it is still being written.
        args.AddRange(["-movflags", "+frag_keyframe+empty_moov+default_base_moof", "-f", "mp4", output]);
        return args;
    }

    public static IReadOnlyList<string> BuildRemuxArgs(string input, string output) =>
    [
        "-hide_banner", "-nostats", "-loglevel", "error", "-y",
        // -shortest again: the live encode overshoots the audio by the encoder's lookahead (~0.5 s of black).
        "-i", input, "-map", "0", "-c", "copy", "-shortest", "-movflags", "+faststart", "-f", "mp4", output
    ];

    /// <summary>Converts to a growing fragmented MP4. With <paramref name="stdin"/> set, input is read from it.</summary>
    public async Task ConvertAsync(
        string input,
        Func<Stream, CancellationToken, Task>? stdin,
        string liveOutput,
        string? title,
        ConversionSettings settings,
        CancellationToken ct)
    {
        await RunAsync(BuildConvertArgs(stdin is null ? input : "pipe:0", liveOutput, title, settings), stdin, ct)
            .ConfigureAwait(false);
    }

    public Task CreatePlaceholderAsync(string output, ConversionSettings s, CancellationToken ct) => RunAsync(
        [
            "-hide_banner", "-nostats", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", $"color=c=black:s={s.Width}x{s.Height}:r={s.FrameRate}",
            "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo",
            "-t", "2", "-map", "0:v:0", "-map", "1:a:0",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-b:a", $"{s.AudioBitrateKbps}k",
            "-movflags", "+faststart", "-f", "mp4", output,
        ],
        null,
        ct);

    /// <summary>
    /// A few-kilobyte file that reports <paramref name="duration"/>: one black frame lasting the whole time plus a
    /// moment of silence. Media servers read the length from it without anything being downloaded or converted.
    /// </summary>
    public Task CreatePlaceholderAsync(string output, ConversionSettings s, TimeSpan duration, CancellationToken ct)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling(duration.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return RunAsync(
            [
                "-hide_banner", "-nostats", "-loglevel", "error", "-y",
                "-f", "lavfi", "-i", $"color=c=black:s={s.Width}x{s.Height}:r=1/{seconds}:d={seconds}",
                "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo:d=0.1",
                "-map", "0:v:0", "-map", "1:a:0",
                "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
                "-c:a", "aac", "-b:a", $"{s.AudioBitrateKbps}k",
                "-movflags", "+faststart", "-f", "mp4", output,
            ],
            null,
            ct);
    }

    public Task RemuxAsync(string input, string output, CancellationToken ct) =>
        RunAsync(BuildRemuxArgs(input, output), null, ct);

    private async Task RunAsync(IReadOnlyList<string> args, Func<Stream, CancellationToken, Task>? stdin, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ffmpegPath())
        {
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            RedirectStandardOutput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        logger.LogDebug("Running ffmpeg {Args}", string.Join(' ', args));
        Process? started;
        try
        {
            started = Process.Start(psi);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            ForgetReadiness();
            throw;
        }

        using var process = started ?? throw new ConversionFailedException("Could not start ffmpeg");
        var stderr = new StringBuilder();
        var stderrTask = Task.Run(
            async () =>
            {
                string? line;
                while ((line = await process.StandardError.ReadLineAsync(CancellationToken.None).ConfigureAwait(false)) is not null)
                {
                    lock (stderr)
                    {
                        if (stderr.Length < 8192)
                        {
                            stderr.AppendLine(line);
                        }
                    }
                }
            },
            CancellationToken.None);

        var exited = false;
        try
        {
            if (stdin is not null)
            {
                await stdin(process.StandardInput.BaseStream, ct).ConfigureAwait(false);
                CloseInput(process);
            }

            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            exited = true;
        }
        finally
        {
            if (!exited)
            {
                // Any failure (cancelled, or the input failed): the child must be gone before the caller frees its
                // conversion slot. Killed before stdin closes, so it can't finish a truncated file as if complete.
                await StopAsync(process, stderrTask).ConfigureAwait(false);
            }
        }

        await stderrTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new ConversionFailedException($"ffmpeg exited with {process.ExitCode}: {stderr.ToString().Trim()}");
        }
    }

    /// <summary>Kills the process tree, then waits (bounded) for it and its stderr reader. Never throws.</summary>
    private async Task StopAsync(Process process, Task stderrTask)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Already exited.
        }

        try
        {
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(StopTimeout).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ffmpeg did not stop within {Timeout}", StopTimeout);
        }

        CloseInput(process);
        try
        {
            await stderrTask.WaitAsync(StopTimeout).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Reading ffmpeg's errors after stopping it failed");
        }
    }

    private static void CloseInput(Process process)
    {
        try
        {
            if (process.StartInfo.RedirectStandardInput)
            {
                process.StandardInput.Close();
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
        }
    }
}
