using System.Buffers;
using System.Buffers.Binary;
using LibNxKit.IO;
using ZstdSharp;

namespace LibNxKit.Ncz;

/// <summary>Restores the original, encrypted NCA from an NCZ (solid or block compressed).</summary>
public static class NczDecompressor
{
    private const int ChunkSize = 1024 * 1024;

    /// <summary>
    /// Decompresses <paramref name="ncz"/> (positioned at the NCZ start, readable and seekable)
    /// into <paramref name="output"/>, writing exactly <paramref name="ncaSize"/> bytes.
    /// </summary>
    /// <param name="ncaSize">The original NCA size (the NCA header's content size).</param>
    /// <param name="onChunk">Optional observer of every output chunk (e.g. for SHA-256 verification).</param>
    public static async Task DecompressAsync(
        Stream ncz,
        Stream output,
        long ncaSize,
        Action<ReadOnlyMemory<byte>>? onChunk = null,
        Action<long>? progress = null,
        CancellationToken ct = default)
    {
        // 1. Raw header region.
        var header = new byte[NczFormat.UncompressedHeaderSize];
        ncz.ReadExactly(header);
        onChunk?.Invoke(header);
        await output.WriteAsync(header, ct).ConfigureAwait(false);
        progress?.Invoke(header.Length);

        // 2. Section table, then an optional block header.
        var sections = NczFormat.ReadSectionTable(ncz);
        var afterSections = ncz.Position;

        var magic = new byte[8];
        var isBlock = ncz.ReadAtLeast(magic, 8, throwOnEndOfStream: false) == 8
            && magic.AsSpan().SequenceEqual(NczFormat.BlockMagic);
        if (!isBlock)
            ncz.Position = afterSections;

        using var crypto = new NczSectionCrypto(sections);
        var remaining = ncaSize - NczFormat.UncompressedHeaderSize;
        long position = NczFormat.UncompressedHeaderSize;

        if (isBlock)
        {
            await DecompressBlocksAsync(ncz, output, crypto, position, remaining, onChunk, progress, ct).ConfigureAwait(false);
            return;
        }

        using var zstd = new DecompressionStream(new NonClosingStream(ncz));
        var buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
        try
        {
            while (remaining > 0)
            {
                ct.ThrowIfCancellationRequested();
                var want = (int)Math.Min(ChunkSize, remaining);
                var got = 0;
                while (got < want)
                {
                    var n = await zstd.ReadAsync(buffer.AsMemory(got, want - got), ct).ConfigureAwait(false);
                    if (n == 0)
                        throw new InvalidDataException($"NCZ stream ended early at NCA offset 0x{position + got:X}.");
                    got += n;
                }

                crypto.Apply(buffer.AsSpan(0, want), position);
                var chunk = buffer.AsMemory(0, want);
                onChunk?.Invoke(chunk);
                await output.WriteAsync(chunk, ct).ConfigureAwait(false);
                progress?.Invoke(want);

                position += want;
                remaining -= want;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task DecompressBlocksAsync(
        Stream ncz,
        Stream output,
        NczSectionCrypto crypto,
        long position,
        long remaining,
        Action<ReadOnlyMemory<byte>>? onChunk,
        Action<long>? progress,
        CancellationToken ct)
    {
        // Block header (after the 8-byte magic): version, type, unused, block size exponent,
        // u32 block count, u64 decompressed size, then u32 compressed size per block.
        var head = new byte[0x10];
        ncz.ReadExactly(head);
        var exponent = head[3];
        if (exponent is < 14 or > 24)
            throw new InvalidDataException("NCZ block size is invalid or unsupported.");
        var blockCount = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(4));
        if (blockCount < 0 || (long)blockCount << exponent < remaining)
            throw new InvalidDataException("NCZ block count is invalid.");
        var decompressedSize = BinaryPrimitives.ReadInt64LittleEndian(head.AsSpan(8));
        var blockSize = 1L << exponent;

        // Tools differ on whether this counts the raw 0x4000-byte header; accept either.
        if (decompressedSize != remaining && decompressedSize != remaining + NczFormat.UncompressedHeaderSize)
            throw new InvalidDataException("NCZ block header size does not match the NCA size.");

        var sizesRaw = new byte[blockCount * 4];
        ncz.ReadExactly(sizesRaw);

        using var decompressor = new Decompressor();
        var compressed = new byte[blockSize];
        var plain = new byte[blockSize];

        for (var i = 0; i < blockCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            var compressedSize = BinaryPrimitives.ReadInt32LittleEndian(sizesRaw.AsSpan(i * 4));
            var plainSize = (int)Math.Min(blockSize, remaining);

            if (compressedSize < 0 || compressedSize > compressed.Length)
                throw new InvalidDataException($"NCZ block {i} has an invalid compressed size.");
            ncz.ReadExactly(compressed.AsSpan(0, compressedSize));

            if (compressedSize < plainSize)
            {
                var written = decompressor.Unwrap(compressed.AsSpan(0, compressedSize), plain.AsSpan(0, plainSize));
                if (written != plainSize)
                    throw new InvalidDataException($"NCZ block {i} decompressed to the wrong size.");
            }
            else
            {
                compressed.AsSpan(0, plainSize).CopyTo(plain);
            }

            crypto.Apply(plain.AsSpan(0, plainSize), position);
            var chunk = plain.AsMemory(0, plainSize);
            onChunk?.Invoke(chunk);
            await output.WriteAsync(chunk, ct).ConfigureAwait(false);
            progress?.Invoke(plainSize);

            position += plainSize;
            remaining -= plainSize;
        }

        if (remaining != 0)
            throw new InvalidDataException("NCZ block data ended before the full NCA was restored.");
    }
}
