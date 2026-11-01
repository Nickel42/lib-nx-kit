using LibNxKit.Crypto;

namespace LibNxKit.IO;

/// <summary>
/// Read-only stream that applies AES-CTR to another stream as it is read. Used to read
/// decrypted NCA sections without materializing them.
/// </summary>
internal sealed class CtrTransformStream : Stream
{
    private readonly Stream _inner;
    private readonly AesCtrCipher _cipher;
    private readonly long _cipherOffset;
    private readonly bool _ownsInner;

    /// <param name="inner">Encrypted data. Position 0 of this stream maps to <paramref name="cipherOffset"/>.</param>
    /// <param name="cipherOffset">Absolute NCA offset of inner position 0, used to build the counter.</param>
    public CtrTransformStream(Stream inner, AesCtrCipher cipher, long cipherOffset, bool ownsInner = true)
    {
        _inner = inner;
        _cipher = cipher;
        _cipherOffset = cipherOffset;
        _ownsInner = ownsInner;
    }

    public override bool CanRead => true;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var position = _inner.Position;
        var read = _inner.Read(buffer);
        if (read > 0)
            _cipher.Transform(buffer[..read], _cipherOffset + position);
        return read;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<int>(Read(buffer.Span));
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cipher.Dispose();
            if (_ownsInner)
                _inner.Dispose();
        }
        base.Dispose(disposing);
    }
}
