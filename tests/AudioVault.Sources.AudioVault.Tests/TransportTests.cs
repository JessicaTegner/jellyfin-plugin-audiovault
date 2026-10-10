using System.Net;
using System.Net.Sockets;
using System.Text;
using AudioVault.Core.Sources;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioVault.Sources.AudioVault.Tests;

/// <summary>
/// A real local HTTP/1.1 server on a socket, for what only the real SocketsHttpHandler does: mocked handlers can't show
/// a request being sent twice.
/// </summary>
internal sealed class SocketSite : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly Func<string, string?> _respond;
    private readonly CancellationTokenSource _stop = new();
    private int _connections;

    /// <param name="respond">The raw response to a request line, or null to close the connection without answering.</param>
    public SocketSite(Func<string, string?> respond)
    {
        _respond = respond;
        _listener.Start();
        _ = AcceptAsync();
    }

    public string BaseUrl => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

    /// <summary>Every complete request received, as "connection#: request line".</summary>
    public List<(int Connection, string Line)> Requests { get; } = [];

    public int Count(string prefix)
    {
        lock (Requests)
        {
            return Requests.Count(r => r.Line.StartsWith(prefix, StringComparison.Ordinal));
        }
    }

    public static string Ok(string body, string type = "text/html") =>
        $"HTTP/1.1 200 OK\r\nContent-Type: {type}\r\nContent-Length: {Encoding.ASCII.GetByteCount(body)}\r\n\r\n{body}";

    public static string Found(string location) => $"HTTP/1.1 302 Found\r\nLocation: {location}\r\nContent-Length: 0\r\n\r\n";

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await _listener.AcceptSocketAsync(_stop.Token);
            }
            catch (Exception)
            {
                return;
            }

            _ = ServeAsync(socket, Interlocked.Increment(ref _connections));
        }
    }

    private async Task ServeAsync(Socket socket, int connection)
    {
        using var _ = socket;
        await using var stream = new NetworkStream(socket);
        var buffer = new List<byte>();
        var chunk = new byte[4096];
        try
        {
            while (true)
            {
                // GETs only: a request is complete at the blank line ending its headers.
                int end;
                while ((end = IndexOfHeadersEnd(buffer)) < 0)
                {
                    var read = await stream.ReadAsync(chunk, _stop.Token);
                    if (read == 0)
                    {
                        return;
                    }

                    buffer.AddRange(chunk.AsSpan(0, read));
                }

                var line = Encoding.ASCII.GetString(buffer.ToArray(), 0, buffer.IndexOf((byte)'\r'));
                buffer.RemoveRange(0, end + 4);
                lock (Requests)
                {
                    Requests.Add((connection, line));
                }

                if (_respond(line) is not { } response)
                {
                    // An orderly close (FIN) after the whole request: the runtime sees the end of the stream, not an error.
                    socket.Shutdown(SocketShutdown.Both);
                    return;
                }

                await stream.WriteAsync(Encoding.ASCII.GetBytes(response), _stop.Token);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
        {
        }
    }

    private static int IndexOfHeadersEnd(List<byte> buffer)
    {
        for (var i = 0; i + 3 < buffer.Count; i++)
        {
            if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
            {
                return i;
            }
        }

        return -1;
    }
}

public class TransportTests
{
    private static (AudioVaultSource Source, SourceAccount Account) Create(SocketSite site)
    {
        var options = new AudioVaultOptions { BaseUrl = site.BaseUrl, MinRequestGap = TimeSpan.Zero, MaxRequestGap = TimeSpan.Zero };
        var token = new AudioVaultCredential(new AudioVaultCredential.RememberToken("remember_web_abc", "token", "127.0.0.1", "/", null), null);

        // No handler factory: the real SocketsHttpHandler the plugin uses in production.
        var source = new AudioVaultSource(() => options, new HttpClient(), NullLogger<AudioVaultSource>.Instance);
        return (source, new SourceAccount("real@example.com", token.Serialize()));
    }

    /// <summary>Signed in: Laravel's guest middleware sends a signed-in visitor away from /login.</summary>
    private static string? SignedInSite(string line, Func<string, string?> download) =>
        line.StartsWith("GET /login ", StringComparison.Ordinal) ? SocketSite.Found("/home")
        : line.StartsWith("GET /download/", StringComparison.Ordinal) ? download(line)
        : SocketSite.Ok("<html></html>");

    [Fact]
    public async Task RuntimeOnItsOwnResendsAGetThatGotNoAnswer()
    {
        // Why the plugin needs its own guard: a plain SocketsHttpHandler repeats the GET up to three times.
        using var site = new SocketSite(_ => null);
        using var client = new HttpClient(new SocketsHttpHandler());

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.GetAsync(site.BaseUrl + "/download/1"));

        Assert.True(site.Count("GET /download/1 ") > 1, $"{site.Count("GET /download/1 ")} requests");
    }

    [Fact]
    public async Task DownloadClosedWithoutAnswerIsSentOnceAndMayHaveCounted()
    {
        using var site = new SocketSite(line => SignedInSite(line, _ => null));
        var (source, account) = Create(site);
        using var _ = source;

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => source.OpenAsync("123", account, default));

        Assert.IsNotType<DownloadNotStartedException>(ex);
        Assert.Equal(1, site.Count("GET /download/123 "));
    }

    [Fact]
    public async Task DownloadOnAReusedConnectionClosedWithoutAnswerIsSentOnce()
    {
        using var site = new SocketSite(line => SignedInSite(
            line,
            l => l.StartsWith("GET /download/1 ", StringComparison.Ordinal) ? SocketSite.Ok("abc", "audio/mpeg") : null));
        var (source, account) = Create(site);
        using var _ = source;

        await using (var media = await source.OpenAsync("1", account, default))
        {
            await media.Content.CopyToAsync(Stream.Null);
        }

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => source.OpenAsync("2", account, default));

        Assert.IsNotType<DownloadNotStartedException>(ex);
        Assert.Equal(1, site.Count("GET /download/2 "));

        // The second download really went out on the first one's pooled connection.
        lock (site.Requests)
        {
            Assert.Equal(
                site.Requests.Single(r => r.Line.StartsWith("GET /download/1 ", StringComparison.Ordinal)).Connection,
                site.Requests.Single(r => r.Line.StartsWith("GET /download/2 ", StringComparison.Ordinal)).Connection);
        }
    }

    [Fact]
    public async Task AnsweredDownloadStreamsOverTheRealHandler()
    {
        using var site = new SocketSite(line => SignedInSite(line, _ => SocketSite.Ok("abc", "audio/mpeg")));
        var (source, account) = Create(site);
        using var _ = source;

        await using var media = await source.OpenAsync("7", account, default);
        using var reader = new StreamReader(media.Content);

        Assert.Equal(SourceMediaKind.SingleAudio, media.Kind);
        Assert.Equal("abc", await reader.ReadToEndAsync());
        Assert.Equal(1, site.Count("GET /download/7 "));
    }
}
