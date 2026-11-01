using System.Buffers.Binary;
using System.Security.Cryptography;

namespace NspXciLibrary.Crypto;

/// <summary>
/// AES-128-XTS as used for NCA headers. Identical to standard XTS except that the
/// sector number is encoded as a big-endian 128-bit value before it is encrypted
/// into the initial tweak.
/// </summary>
internal sealed class NintendoAesXts : IDisposable
{
    public const int SectorSize = 0x200;

    private readonly Aes _dataAes;
    private readonly Aes _tweakAes;

    /// <param name="key">32 bytes: data key followed by tweak key.</param>
    public NintendoAesXts(ReadOnlySpan<byte> key)
    {
        if (key.Length != 0x20)
            throw new ArgumentException("XTS key must be 32 bytes.", nameof(key));

        _dataAes = Aes.Create();
        _dataAes.Key = key[..0x10].ToArray();
        _tweakAes = Aes.Create();
        _tweakAes.Key = key[0x10..].ToArray();
    }

    public void Decrypt(Span<byte> data, long firstSector) => Transform(data, firstSector, encrypt: false);

    public void Encrypt(Span<byte> data, long firstSector) => Transform(data, firstSector, encrypt: true);

    private void Transform(Span<byte> data, long firstSector, bool encrypt)
    {
        if (data.Length % SectorSize != 0)
            throw new ArgumentException("Data length must be a multiple of the sector size.", nameof(data));

        Span<byte> sectorBlock = stackalloc byte[16];
        Span<byte> tweak = stackalloc byte[16];
        var tweaks = new byte[SectorSize];
        var work = new byte[SectorSize];

        var sectorCount = data.Length / SectorSize;
        for (var s = 0; s < sectorCount; s++)
        {
            sectorBlock.Clear();
            BinaryPrimitives.WriteUInt64BigEndian(sectorBlock[8..], (ulong)(firstSector + s));
            _tweakAes.EncryptEcb(sectorBlock, tweak, PaddingMode.None);

            for (var b = 0; b < SectorSize; b += 16)
            {
                tweak.CopyTo(tweaks.AsSpan(b, 16));
                MultiplyByAlpha(tweak);
            }

            var sector = data.Slice(s * SectorSize, SectorSize);
            for (var i = 0; i < SectorSize; i++)
                work[i] = (byte)(sector[i] ^ tweaks[i]);

            if (encrypt)
                _dataAes.EncryptEcb(work, sector, PaddingMode.None);
            else
                _dataAes.DecryptEcb(work, sector, PaddingMode.None);

            for (var i = 0; i < SectorSize; i++)
                sector[i] ^= tweaks[i];
        }
    }

    /// <summary>Multiplies the tweak by x in GF(2^128) (little-endian byte order, as in IEEE 1619).</summary>
    private static void MultiplyByAlpha(Span<byte> tweak)
    {
        var carry = 0;
        for (var i = 0; i < 16; i++)
        {
            var next = tweak[i] >> 7;
            tweak[i] = (byte)((tweak[i] << 1) | carry);
            carry = next;
        }
        if (carry != 0)
            tweak[0] ^= 0x87;
    }

    public void Dispose()
    {
        _dataAes.Dispose();
        _tweakAes.Dispose();
    }
}
