using System.Buffers.Binary;

namespace NoIntrovert.Switch.Nca;

/// <summary>Minimal reader for eShop tickets (.tik), enough to recover the encrypted title key.</summary>
public sealed class Ticket
{
    private Ticket(byte[] rightsId, byte[] encryptedTitleKey, byte titleKeyType, byte masterKeyRevision)
    {
        RightsId = rightsId;
        EncryptedTitleKey = encryptedTitleKey;
        TitleKeyType = titleKeyType;
        MasterKeyRevision = masterKeyRevision;
    }

    public byte[] RightsId { get; }
    public string RightsIdHex => Convert.ToHexString(RightsId);
    public byte[] EncryptedTitleKey { get; }

    /// <summary>0 = common (usable), 1 = personalized (bound to one console; not supported).</summary>
    public byte TitleKeyType { get; }

    public byte MasterKeyRevision { get; }
    public bool IsPersonalized => TitleKeyType == 1;

    public static Ticket Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4)
            throw new InvalidDataException("Ticket is truncated.");
        var sigType = BinaryPrimitives.ReadUInt32LittleEndian(data);
        var dataStart = sigType switch
        {
            0x10000 => 0x240, // RSA-4096 SHA-1
            0x10001 => 0x140, // RSA-2048 SHA-1
            0x10002 => 0x80,  // ECDSA SHA-1
            0x10003 => 0x240, // RSA-4096 SHA-256
            0x10004 => 0x140, // RSA-2048 SHA-256
            0x10005 => 0x80,  // ECDSA SHA-256
            _ => throw new InvalidDataException($"Unknown ticket signature type 0x{sigType:X}."),
        };

        if (data.Length < dataStart + 0x180)
            throw new InvalidDataException("Ticket is truncated.");

        var body = data[dataStart..];
        return new Ticket(
            rightsId: body.Slice(0x160, 0x10).ToArray(),
            encryptedTitleKey: body.Slice(0x40, 0x10).ToArray(),
            titleKeyType: body[0x141],
            masterKeyRevision: body[0x145]);
    }
}
