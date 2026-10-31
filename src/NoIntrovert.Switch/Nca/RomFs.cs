using System.Buffers.Binary;
using System.Text;
using NoIntrovert.Switch.IO;

namespace NoIntrovert.Switch.Nca;

/// <summary>A file inside a RomFS. <see cref="Offset"/> is relative to the stream the RomFS was read from.</summary>
public sealed record RomFsFile(string Name, long Offset, long Size);

/// <summary>Minimal RomFS reader: lists files by name (ignoring directories), enough to find control.nacp.</summary>
public static class RomFs
{
    private const int HeaderSize = 0x50;

    /// <param name="stream">A decrypted section stream.</param>
    /// <param name="romFsOffset">Where the RomFS header starts within <paramref name="stream"/>.</param>
    public static IReadOnlyList<RomFsFile> ListFiles(Stream stream, long romFsOffset)
    {
        var header = StreamUtil.ReadAt(stream, romFsOffset, HeaderSize);
        long Field(int index) => BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(index * 8));

        if (Field(0) != HeaderSize)
            throw new InvalidDataException("RomFS header is corrupt.");

        var fileMetaOffset = Field(7);
        var fileMetaSize = Field(8);
        var dataOffset = Field(9);
        if (fileMetaSize is < 0 or > 0x4000000)
            throw new InvalidDataException("RomFS file table is corrupt.");

        var table = StreamUtil.ReadAt(stream, romFsOffset + fileMetaOffset, (int)fileMetaSize);
        var files = new List<RomFsFile>();
        var pos = 0;
        while (pos + 0x20 <= table.Length)
        {
            var e = table.AsSpan(pos);
            var fileDataOffset = BinaryPrimitives.ReadInt64LittleEndian(e[0x8..]);
            var fileSize = BinaryPrimitives.ReadInt64LittleEndian(e[0x10..]);
            var nameLength = BinaryPrimitives.ReadInt32LittleEndian(e[0x1C..]);
            if (nameLength < 0 || pos + 0x20 + nameLength > table.Length)
                throw new InvalidDataException("RomFS file entry is corrupt.");

            var name = Encoding.UTF8.GetString(e.Slice(0x20, nameLength));
            files.Add(new RomFsFile(name, romFsOffset + dataOffset + fileDataOffset, fileSize));
            pos += 0x20 + (nameLength + 3) / 4 * 4;
        }
        return files;
    }
}
