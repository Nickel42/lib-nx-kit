using System.Security.Cryptography;
using LibNxKit.Crypto;

namespace LibNxKit.Tests;

/// <summary>
/// Vectors produced by an independent implementation (Python 'cryptography': standard AES-XTS fed a
/// big-endian sector tweak, and AES-CTR with a nonce||blockIndex counter). See tests/vectors.py.
/// </summary>
public class CryptoTests
{
    [Fact]
    public void Xts_matches_reference_with_big_endian_sector_tweak()
    {
        var key = Det.Bytes("xtskey", 32);
        var data = Det.Bytes("xtsplain", 0x600);
        Assert.Equal("17D118AB38A4974D23DC3DA43953B6FD32E624B0874BE9E018C589822AE73C35", Convert.ToHexString(key));

        using var xts = new NintendoAesXts(key);
        xts.Encrypt(data, firstSector: 5);
        Assert.Equal("9882C8FA9A64CD49C556DE09906C76724998AC1C100578DEC3ED6B384D7014D3", Convert.ToHexString(SHA256.HashData(data)));

        xts.Decrypt(data, firstSector: 5);
        Assert.Equal(Det.Bytes("xtsplain", 0x600), data);
    }

    [Fact]
    public void Ctr_matches_reference_at_unaligned_offset()
    {
        var key = Det.Bytes("ctrkey", 16);
        var counter = new byte[16];
        Det.Bytes("ctrnonce", 8).CopyTo(counter, 0);
        var data = Det.Bytes("ctrplain", 0x123);

        using var ctr = new AesCtrCipher(key, counter);
        ctr.Transform(data, 0x4007);

        Assert.Equal("9F8BFB35566E9C9BC7EF36C4BE5CFED4", Convert.ToHexString(data.AsSpan(0, 16)));
        Assert.Equal("B48C7EA035F9D3408228147A0EA0F3F6AB9C680554CA334A297F01FC5A40F1A4", Convert.ToHexString(SHA256.HashData(data)));
    }

    [Fact]
    public void Ctr_is_consistent_across_chunk_boundaries()
    {
        var key = Det.Bytes("ctrkey", 16);
        var counter = Det.Bytes("ctrnonce", 16);
        var whole = Det.Bytes("bigctr", 0x20005);
        var pieces = (byte[])whole.Clone();

        using var ctr = new AesCtrCipher(key, counter);
        ctr.Transform(whole, 0x4003);
        ctr.Transform(pieces.AsSpan(0, 0x9999), 0x4003);
        ctr.Transform(pieces.AsSpan(0x9999), 0x4003 + 0x9999);

        Assert.Equal(whole, pieces);
    }
}
