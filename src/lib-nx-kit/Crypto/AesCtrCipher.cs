using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace LibNxKit.Crypto;

/// <summary>
/// AES-128-CTR with Nintendo's counter layout: the upper 8 bytes come from the section's
/// counter (nonce) and the lower 8 bytes are the big-endian block index (byte offset / 16)
/// within the NCA. Encryption and decryption are the same operation.
/// </summary>
internal sealed class AesCtrCipher : IDisposable
{
    private const int ChunkBlocks = 4096; // 64 KiB of keystream per ECB call

    private readonly Aes _aes;
    private readonly byte[] _nonce = new byte[8];
    private readonly byte[] _counters = new byte[ChunkBlocks * 16];
    private readonly byte[] _keystream = new byte[ChunkBlocks * 16];

    /// <param name="key">16-byte AES key.</param>
    /// <param name="counter">16-byte counter; only the first 8 bytes (the nonce) are used.</param>
    public AesCtrCipher(ReadOnlySpan<byte> key, ReadOnlySpan<byte> counter)
    {
        if (key.Length != 0x10)
            throw new ArgumentException("AES key must be 16 bytes.", nameof(key));
        if (counter.Length < 8)
            throw new ArgumentException("Counter must be at least 8 bytes.", nameof(counter));

        _aes = Aes.Create();
        _aes.Key = key.ToArray();
        counter[..8].CopyTo(_nonce);

        // The nonce half of every counter block never changes; fill it once.
        for (var i = 0; i < ChunkBlocks; i++)
            _nonce.CopyTo(_counters, i * 16);
    }

    /// <summary>Encrypts or decrypts <paramref name="data"/> in place.</summary>
    /// <param name="offset">Absolute byte offset of data[0] within the NCA.</param>
    public void Transform(Span<byte> data, long offset)
    {
        var skip = (int)(offset & 0xF);
        var block = (ulong)(offset >> 4);
        var done = 0;

        while (done < data.Length)
        {
            var needed = skip + (data.Length - done);
            var blocks = Math.Min(ChunkBlocks, (needed + 15) / 16);

            for (var i = 0; i < blocks; i++)
                BinaryPrimitives.WriteUInt64BigEndian(_counters.AsSpan(i * 16 + 8, 8), block + (ulong)i);

            var bytes = blocks * 16;
            _aes.EncryptEcb(_counters.AsSpan(0, bytes), _keystream.AsSpan(0, bytes), PaddingMode.None);

            var usable = Math.Min(bytes - skip, data.Length - done);
            var target = data.Slice(done, usable);
            Xor(target, _keystream.AsSpan(skip, usable));

            done += usable;
            block += (ulong)blocks;
            skip = 0;
        }
    }

    /// <summary>target ^= keystream, a vector at a time (the JIT does not vectorize the byte loop).</summary>
    internal static void Xor(Span<byte> target, ReadOnlySpan<byte> keystream)
    {
        var i = 0;
        if (Vector.IsHardwareAccelerated && target.Length >= Vector<byte>.Count)
        {
            var t = MemoryMarshal.Cast<byte, Vector<byte>>(target);
            var k = MemoryMarshal.Cast<byte, Vector<byte>>(keystream[..target.Length]);
            for (var v = 0; v < t.Length; v++)
                t[v] ^= k[v];
            i = t.Length * Vector<byte>.Count;
        }
        for (; i < target.Length; i++)
            target[i] ^= keystream[i];
    }

    public void Dispose() => _aes.Dispose();
}
