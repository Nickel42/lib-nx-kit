namespace NspXciLibrary.IO;

/// <summary>Read-only, seekable view of several files laid end to end (split NSP/XCI parts).</summary>
public sealed class ConcatStream : Stream
{
    private readonly Stream[] _parts;
    private readonly long[] _starts;
    private readonly long _length;
    private long _position;

    public ConcatStream(IReadOnlyList<Stream> parts)
    {
        if (parts.Count == 0)
            throw new ArgumentException("At least one part is required.", nameof(parts));

        _parts = parts.ToArray();
        _starts = new long[_parts.Length];
        long total = 0;
        for (var i = 0; i < _parts.Length; i++)
        {
            if (!_parts[i].CanSeek || !_parts[i].CanRead)
                throw new ArgumentException("Every part must be readable and seekable.", nameof(parts));
            _starts[i] = total;
            total += _parts[i].Length;
        }
        _length = total;
    }

    /// <summary>
    /// Finds the parts of a split package. Accepts a folder of parts named 00, 01, … (the
    /// "archive bit" layout), a first part named *.ns0 / *.xc0 / *.nz0 / *.xz0, or a first part
    /// named *.00. Returns just the given path when it is an ordinary file.
    /// </summary>
    public static IReadOnlyList<string> ResolveParts(string path)
    {
        if (Directory.Exists(path))
        {
            var numbered = Directory.GetFiles(path)
                .Where(f => Path.GetFileName(f).Length == 2 && Path.GetFileName(f).All(char.IsAsciiDigit))
                .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal)
                .ToList();
            if (numbered.Count == 0)
                throw new FileNotFoundException($"Folder '{path}' contains no split parts named 00, 01, ….");
            return numbered;
        }

        var ext = Path.GetExtension(path);
        if (ext.Length == 4 && ext[3] == '0' && ext[1..3].ToLowerInvariant() is "ns" or "xc" or "nz" or "xz")
            return Collect(path, i => Path.ChangeExtension(path, ext[..3] + i.ToString()), maxParts: 10);

        if (ext == ".00")
            return Collect(path, i => Path.ChangeExtension(path, "." + i.ToString("D2")), maxParts: 100);

        return [path];
    }

    private static List<string> Collect(string first, Func<int, string> partName, int maxParts)
    {
        var parts = new List<string> { first };
        for (var i = 1; i < maxParts; i++)
        {
            var next = partName(i);
            if (!File.Exists(next))
                break;
            parts.Add(next);
        }
        return parts;
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
        var total = 0;
        while (buffer.Length > 0 && _position < _length)
        {
            var index = Array.BinarySearch(_starts, _position);
            if (index < 0)
                index = ~index - 1;
            // Skip zero-length parts that share a start offset with the next part.
            while (index < _parts.Length - 1 && _starts[index + 1] <= _position)
                index++;

            var part = _parts[index];
            var inPart = _position - _starts[index];
            var want = (int)Math.Min(buffer.Length, part.Length - inPart);
            part.Position = inPart;
            var read = part.Read(buffer[..want]);
            if (read == 0)
                break;

            _position += read;
            total += read;
            buffer = buffer[read..];
        }
        return total;
    }

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
        if (disposing)
            foreach (var p in _parts)
                p.Dispose();
        base.Dispose(disposing);
    }
}
