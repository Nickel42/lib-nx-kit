using System.Buffers.Binary;
using System.Text;
using LibNxKit.Crypto;
using LibNxKit.Keys;

namespace LibNxKit.Nca;

public enum NcaContentType : byte
{
    Program = 0,
    Meta = 1,
    Control = 2,
    Manual = 3,
    Data = 4,
    PublicData = 5,
}

public enum NcaDistributionType : byte
{
    Download = 0,
    GameCard = 1,
}

public enum NcaEncryptionType : byte
{
    Auto = 0,
    None = 1,
    AesXts = 2,
    AesCtr = 3,
    AesCtrEx = 4, // BKTR (patch RomFS)
    AesCtrSkipLayerHash = 5,
    AesCtrExSkipLayerHash = 6,
}

public enum NcaFsType : byte
{
    RomFs = 0,
    PartitionFs = 1,
}

public enum NcaHashType : byte
{
    Auto = 0,
    None = 1,
    HierarchicalSha256 = 2,
    HierarchicalIntegrity = 3,
}

/// <summary>One of the (up to four) filesystem sections of an NCA.</summary>
public sealed record NcaSection(
    int Index,
    long Offset,
    long Size,
    NcaFsType FsType,
    NcaHashType HashType,
    NcaEncryptionType EncryptionType,
    byte[] Counter,
    byte[] FsHeader)
{
    /// <summary>For HierarchicalSha256 sections, the offset (within the section) of the actual data layer.</summary>
    /// <remarks>Hash data starts at fs header 0x8: master hash (0x20), block size, layer count, then (offset, size) pairs; layer 1 is the data.</remarks>
    public long Sha256DataLayerOffset => BinaryPrimitives.ReadInt64LittleEndian(FsHeader.AsSpan(0x8 + 0x38));

    public long Sha256DataLayerSize => BinaryPrimitives.ReadInt64LittleEndian(FsHeader.AsSpan(0x8 + 0x40));

    /// <summary>
    /// For HierarchicalIntegrity (IVFC) sections such as RomFS, the offset and size (within the section)
    /// of the data level — the last level listed in the IVFC header.
    /// </summary>
    /// <remarks>IVFC header at fs header 0x8: "IVFC", version, master hash size, max layers, then 0x18-byte levels (offset, size, block log2, reserved).</remarks>
    public (long Offset, long Size) IntegrityDataLevel
    {
        get
        {
            var ivfc = FsHeader.AsSpan(0x8);
            if (BinaryPrimitives.ReadUInt32LittleEndian(ivfc) != 0x43465649) // "IVFC"
                throw new InvalidDataException("Section has no IVFC header.");
            var maxLayers = BinaryPrimitives.ReadInt32LittleEndian(ivfc[0xC..]);
            if (maxLayers is < 2 or > 7)
                throw new InvalidDataException("IVFC header is corrupt.");
            var level = ivfc.Slice(0x10 + (maxLayers - 2) * 0x18, 0x18);
            return (BinaryPrimitives.ReadInt64LittleEndian(level), BinaryPrimitives.ReadInt64LittleEndian(level[8..]));
        }
    }
}

/// <summary>A decrypted NCA header (0xC00 bytes, NCA3 format).</summary>
public sealed class NcaHeader
{
    public const int Size = 0xC00;
    public const int MediaUnit = 0x200;

    private readonly byte[] _raw;

    private NcaHeader(byte[] decrypted)
    {
        _raw = decrypted;
        var span = decrypted.AsSpan();

        Magic = Encoding.ASCII.GetString(span.Slice(0x200, 4));
        DistributionType = (NcaDistributionType)span[0x204];
        ContentType = (NcaContentType)span[0x205];
        var keyGenOld = span[0x206];
        KeyAreaKeyKind = (KeyAreaKeyKind)span[0x207];
        ContentSize = BinaryPrimitives.ReadInt64LittleEndian(span[0x208..]);
        ProgramId = BinaryPrimitives.ReadUInt64LittleEndian(span[0x210..]);
        ContentIndex = BinaryPrimitives.ReadUInt32LittleEndian(span[0x218..]);
        SdkVersion = BinaryPrimitives.ReadUInt32LittleEndian(span[0x21C..]);
        KeyGeneration = Math.Max(keyGenOld, span[0x220]);
        RightsId = span.Slice(0x230, 0x10).ToArray();

        var sections = new List<NcaSection>();
        for (var i = 0; i < 4; i++)
        {
            var entry = span.Slice(0x240 + i * 0x10, 0x10);
            var startMedia = BinaryPrimitives.ReadUInt32LittleEndian(entry);
            var endMedia = BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]);
            if (startMedia == 0 && endMedia == 0)
                continue;

            var fsHeader = span.Slice(0x400 + i * 0x200, 0x200).ToArray();
            // The counter nonce is stored little-endian at fs header 0x140; Nintendo's CTR uses it big-endian.
            var counter = new byte[0x10];
            for (var j = 0; j < 8; j++)
                counter[j] = fsHeader[0x140 + 7 - j];

            sections.Add(new NcaSection(
                i,
                (long)startMedia * MediaUnit,
                ((long)endMedia - startMedia) * MediaUnit,
                (NcaFsType)fsHeader[0x2],
                (NcaHashType)fsHeader[0x3],
                (NcaEncryptionType)fsHeader[0x4],
                counter,
                fsHeader));
        }
        Sections = sections;
    }

    public string Magic { get; }
    public NcaDistributionType DistributionType { get; }
    public NcaContentType ContentType { get; }
    public KeyAreaKeyKind KeyAreaKeyKind { get; }
    public long ContentSize { get; }
    public ulong ProgramId { get; }
    public uint ContentIndex { get; }
    public uint SdkVersion { get; }

    /// <summary>The effective key generation (max of the old and new fields).</summary>
    public byte KeyGeneration { get; }

    /// <summary>Master key revision used for this NCA's keys.</summary>
    public int MasterKeyRevision => KeyGeneration == 0 ? 0 : KeyGeneration - 1;

    public byte[] RightsId { get; }
    public bool HasRightsId => RightsId.AsSpan().ContainsAnyExcept((byte)0);
    public IReadOnlyList<NcaSection> Sections { get; }

    /// <summary>The still-encrypted 0x40-byte key area.</summary>
    public ReadOnlySpan<byte> EncryptedKeyArea => _raw.AsSpan(0x300, 0x40);

    /// <summary>A copy of the full decrypted header.</summary>
    public byte[] ToArray() => (byte[])_raw.Clone();

    /// <summary>Decrypts and parses the first 0xC00 bytes of an NCA.</summary>
    public static NcaHeader Decrypt(ReadOnlySpan<byte> encrypted, KeySet keys)
    {
        if (encrypted.Length < Size)
            throw new ArgumentException("NCA header must be at least 0xC00 bytes.", nameof(encrypted));

        var buffer = encrypted[..Size].ToArray();
        using (var xts = new NintendoAesXts(keys.HeaderKey))
            xts.Decrypt(buffer, 0);

        var magic = Encoding.ASCII.GetString(buffer, 0x200, 4);
        if (magic != "NCA3")
        {
            if (magic is "NCA2" or "NCA0")
                throw new NotSupportedException($"{magic} headers are not supported (very old content).");
            throw new InvalidDataException("NCA header did not decrypt. The file is corrupt or header_key is wrong.");
        }

        return new NcaHeader(buffer);
    }

    /// <summary>Encrypts a modified header back to its on-disk form.</summary>
    public static byte[] Encrypt(ReadOnlySpan<byte> decrypted, KeySet keys)
    {
        var buffer = decrypted[..Size].ToArray();
        using var xts = new NintendoAesXts(keys.HeaderKey);
        xts.Encrypt(buffer, 0);
        return buffer;
    }
}
