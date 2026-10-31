using System.Buffers.Binary;

namespace NoIntrovert.Switch.Meta;

public enum ContentMetaType : byte
{
    SystemProgram = 0x01,
    SystemData = 0x02,
    SystemUpdate = 0x03,
    BootImagePackage = 0x04,
    BootImagePackageSafe = 0x05,
    Application = 0x80,
    Patch = 0x81,
    AddOnContent = 0x82,
    Delta = 0x83,
    DataPatch = 0x84,
}

public enum CnmtContentType : byte
{
    Meta = 0,
    Program = 1,
    Data = 2,
    Control = 3,
    HtmlDocument = 4,
    LegalInformation = 5,
    DeltaFragment = 6,
}

/// <summary>One content (NCA) referenced by a content meta.</summary>
public sealed record CnmtContent(byte[] Hash, byte[] ContentId, long Size, CnmtContentType Type, byte IdOffset)
{
    public string ContentIdHex => Convert.ToHexString(ContentId).ToLowerInvariant();
}

/// <summary>A parsed packaged content meta (.cnmt) — describes one title (game, update or DLC).</summary>
public sealed class Cnmt
{
    private Cnmt(ulong titleId, uint version, ContentMetaType type, ulong relatedTitleId, uint requiredVersion,
        IReadOnlyList<CnmtContent> contents)
    {
        TitleId = titleId;
        Version = version;
        Type = type;
        RelatedTitleId = relatedTitleId;
        RequiredVersion = requiredVersion;
        Contents = contents;
    }

    public ulong TitleId { get; }
    public uint Version { get; }
    public ContentMetaType Type { get; }

    /// <summary>For Application: its patch ID. For Patch/AddOnContent: the base application ID.</summary>
    public ulong RelatedTitleId { get; }

    /// <summary>Required system version (Application/Patch) or required application version (AddOnContent).</summary>
    public uint RequiredVersion { get; }

    public IReadOnlyList<CnmtContent> Contents { get; }

    /// <summary>The base application this title belongs to.</summary>
    public ulong ApplicationId => Type switch
    {
        ContentMetaType.Application => TitleId,
        ContentMetaType.Patch => RelatedTitleId != 0 ? RelatedTitleId : TitleId & ~0x800UL,
        ContentMetaType.AddOnContent => RelatedTitleId != 0 ? RelatedTitleId : (TitleId - 0x1000) & ~0xFFFUL,
        ContentMetaType.Delta => TitleId & ~0xFFFUL,
        _ => TitleId,
    };

    public static Cnmt Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 0x20)
            throw new InvalidDataException("CNMT is truncated.");

        var titleId = BinaryPrimitives.ReadUInt64LittleEndian(data);
        var version = BinaryPrimitives.ReadUInt32LittleEndian(data[0x8..]);
        var type = (ContentMetaType)data[0xC];
        var extHeaderSize = BinaryPrimitives.ReadUInt16LittleEndian(data[0xE..]);
        var contentCount = BinaryPrimitives.ReadUInt16LittleEndian(data[0x10..]);
        if (data.Length < 0x20 + extHeaderSize + contentCount * 0x38)
            throw new InvalidDataException("CNMT is truncated.");

        ulong related = 0;
        uint required = 0;
        var ext = data.Slice(0x20, extHeaderSize);
        if ((type is ContentMetaType.Application or ContentMetaType.Patch or ContentMetaType.AddOnContent) && ext.Length >= 0xC)
        {
            related = BinaryPrimitives.ReadUInt64LittleEndian(ext);
            required = BinaryPrimitives.ReadUInt32LittleEndian(ext[0x8..]);
        }

        var contents = new List<CnmtContent>(contentCount);
        var recordStart = 0x20 + extHeaderSize;
        for (var i = 0; i < contentCount; i++)
        {
            var r = data.Slice(recordStart + i * 0x38, 0x38);
            // 40-bit little-endian size; the 6th byte became a content-attributes field in newer firmware.
            long size = 0;
            for (var b = 4; b >= 0; b--)
                size = (size << 8) | r[0x30 + b];

            contents.Add(new CnmtContent(
                Hash: r[..0x20].ToArray(),
                ContentId: r.Slice(0x20, 0x10).ToArray(),
                Size: size,
                Type: (CnmtContentType)r[0x36],
                IdOffset: r[0x37]));
        }

        return new Cnmt(titleId, version, type, related, required, contents);
    }
}
