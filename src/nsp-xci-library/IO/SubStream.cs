namespace NspXciLibrary.IO;

/// <summary>
/// A read-only, seekable window over part of another stream. Several sub-streams may share one
/// base stream as long as they are not read concurrently; each read re-seeks the base stream.
/// </summary>
public sealed class SubStream : Stream
{
    private readonly Stream _base;
    private readonly long _start;
    private readonly long _length;
    private readonly bool _ownsBase;
    private long _position;

    public SubStream(Stream baseStream, long start, long length, bool ownsBase = false)
    {
        if (!baseStream.CanSeek || !baseStream.CanRead)
            throw new ArgumentException("Base stream must be readable and seekable.", nameof(baseStream));
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        _base = baseStream;
        _start = start;
        _length = length;
        _ownsBase = ownsBase;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => _position = value < 0 ? throw new ArgumentOutOfRangeException(nameof(value)) : value;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var remaining = _length - _position;
        if (remaining <= 0)
            return 0;

        var toRead = (int)Math.Min(buffer.Length, remaining);
        _base.Position = _start + _position;
        var read = _base.Read(buffer[..toRead]);
        _position += read;
        return read;
    }

    // The base is a local file and reads are already large; avoid Stream's thread-pool BeginRead fallback.
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<int>(Read(buffer.Span));
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        return _position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && _ownsBase)
            _base.Dispose();
        base.Dispose(disposing);
    }
}
