using System.Buffers.Binary;
using System.Text;
using NspXciLibrary.IO;

namespace NspXciLibrary.Containers;

/// <summary>A file inside a PFS0 or HFS0 partition. <see cref="Offset"/> is absolute within the outer stream.</summary>
public sealed record PartitionEntry(string Name, long Offset, long Size);

/// <summary>Reader and header builder for PFS0 (NSP/NSZ and NCA section filesystems).</summary>
public static class Pfs0
{
    public const uint Magic = 0x30534650; // "PFS0"
    private const int HeaderSize = 0x10;
    private const int EntrySize = 0x18;

    public static bool IsPfs0(Stream stream, long offset = 0)
    {
        if (stream.Length < offset + HeaderSize)
            return false;
        var head = StreamUtil.ReadAt(stream, offset, 4);
        return BinaryPrimitives.ReadUInt32LittleEndian(head) == Magic;
    }

    /// <summary>Reads the entry table of a PFS0 that starts at <paramref name="offset"/>.</summary>
    public static IReadOnlyList<PartitionEntry> Read(Stream stream, long offset = 0)
    {
        var header = StreamUtil.ReadAt(stream, offset, HeaderSize);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic)
            throw new InvalidDataException("Not a PFS0 partition.");

        var count = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        var stringTableSize = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8));
        if (count < 0 || count > 0x10000 || stringTableSize < 0 || stringTableSize > 0x1000000)
            throw new InvalidDataException("PFS0 header is corrupt.");

        var tableBytes = StreamUtil.ReadAt(stream, offset + HeaderSize, count * EntrySize + stringTableSize);
        var strings = tableBytes.AsSpan(count * EntrySize);
        var dataStart = offset + HeaderSize + count * EntrySize + stringTableSize;

        var entries = new List<PartitionEntry>(count);
        for (var i = 0; i < count; i++)
        {
            var e = tableBytes.AsSpan(i * EntrySize, EntrySize);
            var dataOffset = BinaryPrimitives.ReadInt64LittleEndian(e);
            var size = BinaryPrimitives.ReadInt64LittleEndian(e[8..]);
            var nameOffset = BinaryPrimitives.ReadInt32LittleEndian(e[16..]);
            entries.Add(new PartitionEntry(StreamUtil.ReadAsciiZ(strings, nameOffset), dataStart + dataOffset, size));
        }
        return entries;
    }

    /// <summary>
    /// Builds a PFS0 header for files laid out back to back in the given order. The string table
    /// is padded so file data begins on a 0x20-byte boundary.
    /// </summary>
    public static byte[] BuildHeader(IReadOnlyList<(string Name, long Size)> files)
    {
        var nameBytes = files.Select(f => Encoding.UTF8.GetBytes(f.Name)).ToList();
        var rawStringTable = nameBytes.Sum(n => n.Length + 1);
        var unpadded = HeaderSize + files.Count * EntrySize + rawStringTable;
        var headerSize = (int)StreamUtil.AlignUp(unpadded, 0x20);
        if (headerSize == unpadded)
            headerSize += 0x20; // always leave some padding, as common tools do
        var stringTableSize = headerSize - HeaderSize - files.Count * EntrySize;

        var header = new byte[headerSize];
        var span = header.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, Magic);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], files.Count);
        BinaryPrimitives.WriteInt32LittleEndian(span[8..], stringTableSize);

        long dataOffset = 0;
        var nameOffset = 0;
        var stringsStart = HeaderSize + files.Count * EntrySize;
        for (var i = 0; i < files.Count; i++)
        {
            var e = span.Slice(HeaderSize + i * EntrySize, EntrySize);
            BinaryPrimitives.WriteInt64LittleEndian(e, dataOffset);
            BinaryPrimitives.WriteInt64LittleEndian(e[8..], files[i].Size);
            BinaryPrimitives.WriteInt32LittleEndian(e[16..], nameOffset);

            nameBytes[i].CopyTo(span[(stringsStart + nameOffset)..]);
            nameOffset += nameBytes[i].Length + 1;
            dataOffset += files[i].Size;
        }
        return header;
    }
}

/// <summary>An HFS0 entry to write. <see cref="Offset"/> is relative to the end of the HFS0 header.</summary>
public sealed record Hfs0BuildEntry(string Name, long Offset, long Size, int HashedSize, byte[] Hash);

/// <summary>Reader and header builder for HFS0 (the hashed partitions inside XCI/XCZ game card images).</summary>
public static class Hfs0
{
    public const uint Magic = 0x30534648; // "HFS0"
    private const int HeaderSize = 0x10;
    private const int EntrySize = 0x40;

    /// <summary>
    /// Builds an HFS0 header, padded to a multiple of 0x200. Its length depends only on the names,
    /// so a placeholder can be written first and replaced once sizes and hashes are known.
    /// </summary>
    public static byte[] BuildHeader(IReadOnlyList<Hfs0BuildEntry> entries)
    {
        var nameBytes = entries.Select(e => Encoding.UTF8.GetBytes(e.Name)).ToList();
        var rawStrings = nameBytes.Sum(n => n.Length + 1);
        var unpadded = HeaderSize + entries.Count * EntrySize + rawStrings;
        var headerSize = (int)StreamUtil.AlignUp(Math.Max(unpadded, 1), 0x200);
        var stringTableSize = headerSize - HeaderSize - entries.Count * EntrySize;

        var header = new byte[headerSize];
        var span = header.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, Magic);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], entries.Count);
        BinaryPrimitives.WriteInt32LittleEndian(span[8..], stringTableSize);

        var stringsStart = HeaderSize + entries.Count * EntrySize;
        var nameOffset = 0;
        for (var i = 0; i < entries.Count; i++)
        {
            var e = span.Slice(HeaderSize + i * EntrySize, EntrySize);
            var entry = entries[i];
            BinaryPrimitives.WriteInt64LittleEndian(e, entry.Offset);
            BinaryPrimitives.WriteInt64LittleEndian(e[0x8..], entry.Size);
            BinaryPrimitives.WriteInt32LittleEndian(e[0x10..], nameOffset);
            BinaryPrimitives.WriteInt32LittleEndian(e[0x14..], entry.HashedSize);
            entry.Hash.AsSpan(0, 0x20).CopyTo(e[0x20..]);

            nameBytes[i].CopyTo(span[(stringsStart + nameOffset)..]);
            nameOffset += nameBytes[i].Length + 1;
        }
        return header;
    }

    public static IReadOnlyList<PartitionEntry> Read(Stream stream, long offset)
    {
        var header = StreamUtil.ReadAt(stream, offset, HeaderSize);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic)
            throw new InvalidDataException("Not an HFS0 partition.");

        var count = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        var stringTableSize = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8));
        if (count < 0 || count > 0x10000 || stringTableSize < 0 || stringTableSize > 0x1000000)
            throw new InvalidDataException("HFS0 header is corrupt.");

        var tableBytes = StreamUtil.ReadAt(stream, offset + HeaderSize, count * EntrySize + stringTableSize);
        var strings = tableBytes.AsSpan(count * EntrySize);
        var dataStart = offset + HeaderSize + count * EntrySize + stringTableSize;

        var entries = new List<PartitionEntry>(count);
        for (var i = 0; i < count; i++)
        {
            var e = tableBytes.AsSpan(i * EntrySize, EntrySize);
            var dataOffset = BinaryPrimitives.ReadInt64LittleEndian(e);
            var size = BinaryPrimitives.ReadInt64LittleEndian(e[8..]);
            var nameOffset = BinaryPrimitives.ReadInt32LittleEndian(e[16..]);
            entries.Add(new PartitionEntry(StreamUtil.ReadAsciiZ(strings, nameOffset), dataStart + dataOffset, size));
        }
        return entries;
    }
}
