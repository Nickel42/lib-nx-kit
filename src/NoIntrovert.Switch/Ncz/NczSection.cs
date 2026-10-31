using System.Buffers.Binary;
using System.Text;
using NoIntrovert.Switch.Crypto;
using NoIntrovert.Switch.IO;

namespace NoIntrovert.Switch.Ncz;

/// <summary>An entry of the NCZ section table (0x40 bytes on disk).</summary>
public sealed record NczSection(long Offset, long Size, long CryptoType, byte[] CryptoKey, byte[] CryptoCounter)
{
    public const long CryptoNone = 1;
    public const long CryptoCtr = 3;
    public const long CryptoCtrEx = 4;

    public long End => Offset + Size;
    public bool IsEncrypted => CryptoType is CryptoCtr or CryptoCtrEx;
}

/// <summary>Constants and table I/O shared by the NCZ reader and writer.</summary>
public static class NczFormat
{
    /// <summary>Bytes at the start of an NCA that are stored verbatim (encrypted) in an NCZ.</summary>
    public const int UncompressedHeaderSize = 0x4000;

    public static readonly byte[] SectionMagic = Encoding.ASCII.GetBytes("NCZSECTN");
    public static readonly byte[] BlockMagic = Encoding.ASCII.GetBytes("NCZBLOCK");

    public static byte[] WriteSectionTable(IReadOnlyList<NczSection> sections)
    {
        var buffer = new byte[0x10 + sections.Count * 0x40];
        var span = buffer.AsSpan();
        SectionMagic.CopyTo(span);
        BinaryPrimitives.WriteInt64LittleEndian(span[8..], sections.Count);
        for (var i = 0; i < sections.Count; i++)
        {
            var e = span.Slice(0x10 + i * 0x40, 0x40);
            var s = sections[i];
            BinaryPrimitives.WriteInt64LittleEndian(e, s.Offset);
            BinaryPrimitives.WriteInt64LittleEndian(e[0x8..], s.Size);
            BinaryPrimitives.WriteInt64LittleEndian(e[0x10..], s.CryptoType);
            s.CryptoKey.AsSpan(0, 0x10).CopyTo(e[0x20..]);
            s.CryptoCounter.AsSpan(0, 0x10).CopyTo(e[0x30..]);
        }
        return buffer;
    }

    /// <summary>Reads the section table at the current position of <paramref name="stream"/>.</summary>
    public static List<NczSection> ReadSectionTable(Stream stream)
    {
        Span<byte> head = stackalloc byte[0x10];
        stream.ReadExactly(head);
        if (!head[..8].SequenceEqual(SectionMagic))
            throw new InvalidDataException("NCZ section table magic not found.");

        var count = BinaryPrimitives.ReadInt64LittleEndian(head[8..]);
        if (count is < 0 or > 64)
            throw new InvalidDataException("NCZ section table is corrupt.");

        var table = new byte[count * 0x40];
        stream.ReadExactly(table);
        var sections = new List<NczSection>((int)count);
        for (var i = 0; i < count; i++)
        {
            var e = table.AsSpan(i * 0x40, 0x40);
            sections.Add(new NczSection(
                BinaryPrimitives.ReadInt64LittleEndian(e),
                BinaryPrimitives.ReadInt64LittleEndian(e[0x8..]),
                BinaryPrimitives.ReadInt64LittleEndian(e[0x10..]),
                e.Slice(0x20, 0x10).ToArray(),
                e.Slice(0x30, 0x10).ToArray()));
        }
        return sections;
    }
}

/// <summary>
/// Applies (or removes — CTR is symmetric) each section's encryption to NCA bytes by absolute offset.
/// Bytes outside any encrypted section pass through unchanged.
/// </summary>
internal sealed class NczSectionCrypto : IDisposable
{
    private readonly List<(NczSection Section, AesCtrCipher Cipher)> _encrypted = new();

    public NczSectionCrypto(IEnumerable<NczSection> sections)
    {
        foreach (var s in sections.Where(s => s.IsEncrypted))
            _encrypted.Add((s, new AesCtrCipher(s.CryptoKey, s.CryptoCounter)));
    }

    public void Apply(Span<byte> data, long absoluteOffset)
    {
        var dataEnd = absoluteOffset + data.Length;
        foreach (var (s, cipher) in _encrypted)
        {
            var start = Math.Max(s.Offset, absoluteOffset);
            var end = Math.Min(s.End, dataEnd);
            if (start >= end)
                continue;
            cipher.Transform(data.Slice((int)(start - absoluteOffset), (int)(end - start)), start);
        }
    }

    public void Dispose()
    {
        foreach (var (_, cipher) in _encrypted)
            cipher.Dispose();
    }
}

/// <summary>A stream wrapper that ignores Dispose, so codec streams can't close our file handles.</summary>
internal sealed class NonClosingStream(Stream inner) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override int Read(Span<byte> buffer) => inner.Read(buffer);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => inner.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => inner.WriteAsync(buffer, ct);
    protected override void Dispose(bool disposing) { /* intentionally not disposing inner */ }
}
