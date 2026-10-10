namespace AudioVault.Sources.AudioVault;

/// <summary>
/// Wraps a download connection so SocketsHttpHandler never resends a request by itself. Its HTTP/1.1 connection marks a
/// request as safe to retry when the server closes the connection before any response byte (HttpConnection._canRetry,
/// retried up to 3 times by HttpConnectionPool). Here that end of stream becomes an error, which the runtime does not retry:
/// the server may have accepted the request before closing, so it may already have counted as a download.
/// </summary>
internal sealed class NoReplayStream(Stream inner) : Stream
{
    // Set by writes, cleared by the first byte of the answer: a request is on the wire without a response yet.
    private volatile bool _awaitingResponse;
    private volatile bool _ended;

    public override bool CanRead => inner.CanRead;

    public override bool CanSeek => false;

    public override bool CanWrite => inner.CanWrite;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Check(inner.Read(buffer, offset, count), count);

    public override int Read(Span<byte> buffer) => Check(inner.Read(buffer), buffer.Length);

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Check(await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false), count);

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Check(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false), buffer.Length);

    public override void Write(byte[] buffer, int offset, int count)
    {
        BeforeWrite();
        inner.Write(buffer, offset, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        BeforeWrite();
        inner.Write(buffer);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        BeforeWrite();
        return inner.WriteAsync(buffer, offset, count, cancellationToken);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        BeforeWrite();
        return inner.WriteAsync(buffer, cancellationToken);
    }

    public override void Flush() => inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    private void BeforeWrite()
    {
        // A pooled connection whose idle check saw the server hang up a moment before this request was written:
        // refuse to write rather than let the runtime see that end of stream as a retryable failure.
        if (_ended)
        {
            throw new IOException("AudioVault closed the connection; not sending the download request on it.");
        }

        _awaitingResponse = true;
    }

    private int Check(int read, int requested)
    {
        if (read > 0)
        {
            _awaitingResponse = false;
        }
        else if (requested > 0)
        {
            _ended = true;
            if (_awaitingResponse)
            {
                throw new HttpIOException(
                    HttpRequestError.ResponseEnded,
                    "AudioVault closed the connection without answering the download request; not sending it again.");
            }
        }

        return read;
    }
}
