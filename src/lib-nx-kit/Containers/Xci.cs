using System.Buffers.Binary;
using LibNxKit.IO;

namespace LibNxKit.Containers;

/// <summary>Reads the game card image layout (XCI/XCZ) down to the secure partition.</summary>
public static class Xci
{
    public const uint HeadMagic = 0x44414548; // "HEAD"

    /// <summary>
    /// Returns the offset of the XCI header's signature block (0 for normal dumps,
    /// 0x1000 for dumps that include the card's initial-data key area), or -1 if not an XCI.
    /// </summary>
    public static long FindHeaderBase(Stream stream)
    {
        foreach (var candidate in new long[] { 0, 0x1000 })
        {
            if (stream.Length < candidate + 0x200)
                continue;
            var magic = StreamUtil.ReadAt(stream, candidate + 0x100, 4);
            if (BinaryPrimitives.ReadUInt32LittleEndian(magic) == HeadMagic)
                return candidate;
        }
        return -1;
    }

    /// <summary>Offset at which writers place the root HFS0 (the usual layout).</summary>
    public const long RootPartitionOffset = 0xF000;

    /// <summary>
    /// Builds a 0x200-byte card header. The RSA signature and the encrypted card-info block are left
    /// zeroed: emulators and installers don't check them, and real ones can't be produced without
    /// Nintendo's keys.
    /// </summary>
    public static byte[] BuildHeader(long rootOffset, byte[] rootHeader, long secureOffset, long totalSize, ulong packageId)
    {
        var header = new byte[0x200];
        var h = header.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(h[0x100..], HeadMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(h[0x104..], (uint)(secureOffset / 0x200)); // secure area start page
        BinaryPrimitives.WriteUInt32LittleEndian(h[0x108..], 0xFFFFFFFF);                   // backup area: none
        h[0x10C] = 0;                                                                       // kek index
        h[0x10D] = CardSizeCode(totalSize);
        h[0x10E] = 0;                                                                       // header version
        h[0x10F] = 0;                                                                       // flags
        BinaryPrimitives.WriteUInt64LittleEndian(h[0x110..], packageId);
        var validDataEndPage = (ulong)(totalSize / 0x200 - 1);
        BinaryPrimitives.WriteUInt64LittleEndian(h[0x118..], validDataEndPage);
        BinaryPrimitives.WriteInt64LittleEndian(h[0x130..], rootOffset);
        BinaryPrimitives.WriteInt64LittleEndian(h[0x138..], rootHeader.Length);
        System.Security.Cryptography.SHA256.HashData(rootHeader).CopyTo(h[0x140..]);
        BinaryPrimitives.WriteUInt32LittleEndian(h[0x180..], 1);                            // SelSec
        BinaryPrimitives.WriteUInt32LittleEndian(h[0x184..], 2);                            // SelT1Key
        BinaryPrimitives.WriteUInt32LittleEndian(h[0x188..], 0);                            // SelKey
        BinaryPrimitives.WriteUInt32LittleEndian(h[0x18C..], (uint)validDataEndPage);       // LimArea
        return header;
    }

    /// <summary>The smallest standard cartridge size that holds <paramref name="totalSize"/> bytes.</summary>
    public static byte CardSizeCode(long totalSize)
    {
        const long GiB = 1L << 30;
        return totalSize switch
        {
            <= 1 * GiB => 0xFA,
            <= 2 * GiB => 0xF8,
            <= 4 * GiB => 0xF0,
            <= 8 * GiB => 0xE0,
            <= 16 * GiB => 0xE1,
            _ => 0xE2, // 32 GB
        };
    }

    /// <summary>Lists the files in the secure partition, which holds the game's NCAs and NCZs.</summary>
    public static IReadOnlyList<PartitionEntry> ReadSecurePartition(Stream stream)
    {
        var baseOffset = FindHeaderBase(stream);
        if (baseOffset < 0)
            throw new InvalidDataException("Not an XCI game card image.");

        var header = StreamUtil.ReadAt(stream, baseOffset, 0x200);
        var rootOffset = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(0x130));

        var root = Hfs0.Read(stream, baseOffset + rootOffset);
        var secure = root.FirstOrDefault(p => p.Name == "secure")
            ?? throw new InvalidDataException("XCI has no secure partition.");

        return Hfs0.Read(stream, secure.Offset);
    }
}
