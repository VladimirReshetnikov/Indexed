using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Indexed.Service;

/// <summary>
/// A read-only <see cref="Stream"/> wrapper that throws
/// <see cref="InvalidOperationException"/> if more than
/// <paramref name="maxBytes"/> are consumed from the inner stream.
/// </summary>
/// <remarks>
/// Used to guard request-body deserialization against oversized payloads
/// when the <c>Content-Length</c> header is absent or untrustworthy (e.g.,
/// chunked transfer encoding, malicious clients).
/// </remarks>
internal sealed class LengthLimitingStream(Stream inner, long maxBytes) : Stream
{
    private long _totalRead;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var n = inner.Read(buffer, offset, count);
        _totalRead += n;
        if (_totalRead > maxBytes)
            throw new InvalidOperationException($"request body exceeded {maxBytes} byte limit");
        return n;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var n = await inner.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
        _totalRead += n;
        if (_totalRead > maxBytes)
            throw new InvalidOperationException($"request body exceeded {maxBytes} byte limit");
        return n;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var n = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        _totalRead += n;
        if (_totalRead > maxBytes)
            throw new InvalidOperationException($"request body exceeded {maxBytes} byte limit");
        return n;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        // Do NOT dispose inner — it's owned by the HttpListenerRequest.
        base.Dispose(disposing);
    }
}
