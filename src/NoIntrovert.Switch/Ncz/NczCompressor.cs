using System.Buffers;
using System.Buffers.Binary;
using NoIntrovert.Switch.IO;
using NoIntrovert.Switch.Nca;
using ZstdSharp;
using ZstdSharp.Unsafe;

namespace NoIntrovert.Switch.Ncz;

public enum NczMode
{
    /// <summary>
    /// One zstd stream for the whole NCA. Smallest output; the nsz default. zstd's own worker threads
    /// compress it in parallel jobs, still producing a single standard frame.
    /// </summary>
    Solid,

    /// <summary>Independent blocks compressed in parallel. Much faster on multi-core machines.</summary>
    Block,
}

public sealed record NczOptions
{
    /// <summary>zstd level 1–22. nsz defaults to 18.</summary>
    public int Level { get; init; } = 18;

    public NczMode Mode { get; init; } = NczMode.Solid;

    /// <summary>Block size as a power of two (block mode only). 20 = 1 MiB.</summary>
    public int BlockSizeExponent { get; init; } = 20;

    /// <summary>
    /// Threads used for compression: parallel blocks in block mode, zstd workers in solid mode.
    /// Defaults to the processor count; 1 compresses solid mode on the calling thread.
    /// </summary>
    public int MaxDegreeOfParallelism { get; init; } = Environment.ProcessorCount;
}

/// <summary>Compresses an NCA into NCZ: the section data is decrypted, then zstd-compressed.</summary>
public static class NczCompressor
{
    private const int ChunkSize = 1024 * 1024;

    /// <summary>Builds the section table for an NCA, covering everything from 0x4000 to the end.</summary>
    public static List<NczSection> BuildSections(NcaReader reader, long ncaLength)
    {
        var sections = new List<NczSection>();
        long cursor = NczFormat.UncompressedHeaderSize;

        foreach (var s in reader.Header.Sections.OrderBy(s => s.Offset))
        {
            var start = Math.Max(Math.Max(s.Offset, NczFormat.UncompressedHeaderSize), cursor);
            var end = Math.Min(s.Offset + s.Size, ncaLength);
            if (end <= start)
                continue;

            if (start > cursor)
                sections.Add(Plain(cursor, start - cursor));

            sections.Add(s.EncryptionType switch
            {
                NcaEncryptionType.None => Plain(start, end - start),
                NcaEncryptionType.AesCtr or NcaEncryptionType.AesCtrSkipLayerHash
                    => new NczSection(start, end - start, NczSection.CryptoCtr, reader.GetContentKey(), s.Counter),
                NcaEncryptionType.AesCtrEx or NcaEncryptionType.AesCtrExSkipLayerHash
                    => new NczSection(start, end - start, NczSection.CryptoCtrEx, reader.GetContentKey(), s.Counter),
                _ => throw new NotSupportedException($"Cannot compress a section with {s.EncryptionType} encryption."),
            });
            cursor = end;
        }

        if (cursor < ncaLength)
            sections.Add(Plain(cursor, ncaLength - cursor));
        return sections;
    }

    private static NczSection Plain(long offset, long size)
        => new(offset, size, NczSection.CryptoNone, new byte[0x10], new byte[0x10]);

    /// <summary>
    /// Compresses <paramref name="nca"/> (a readable, seekable stream containing exactly one NCA)
    /// to <paramref name="output"/>. Returns the number of bytes written.
    /// </summary>
    /// <param name="onSourceChunk">Optional observer of the original NCA bytes in order (e.g. for SHA-256 verification).</param>
    public static async Task<long> CompressAsync(
        Stream nca,
        NcaReader reader,
        Stream output,
        NczOptions options,
        Action<ReadOnlyMemory<byte>>? onSourceChunk = null,
        Action<long>? progress = null,
        CancellationToken ct = default)
    {
        if (nca.Length <= NczFormat.UncompressedHeaderSize)
            throw new ArgumentException("NCA is too small to compress.", nameof(nca));

        var outputStart = output.Position;
        var sections = BuildSections(reader, nca.Length);

        var header = StreamUtil.ReadAt(nca, 0, NczFormat.UncompressedHeaderSize);
        onSourceChunk?.Invoke(header);
        await output.WriteAsync(header, ct).ConfigureAwait(false);
        await output.WriteAsync(NczFormat.WriteSectionTable(sections), ct).ConfigureAwait(false);
        progress?.Invoke(header.Length);

        using var crypto = new NczSectionCrypto(sections);
        nca.Position = NczFormat.UncompressedHeaderSize;
        var remaining = nca.Length - NczFormat.UncompressedHeaderSize;

        if (options.Mode == NczMode.Solid)
            await CompressSolidAsync(nca, output, crypto, remaining, options.Level, options.MaxDegreeOfParallelism, onSourceChunk, progress, ct).ConfigureAwait(false);
        else
            await CompressBlocksAsync(nca, output, crypto, remaining, options, onSourceChunk, progress, ct).ConfigureAwait(false);

        return output.Position - outputStart;
    }

    private static async Task CompressSolidAsync(
        Stream nca, Stream output, NczSectionCrypto crypto, long remaining, int level, int workers,
        Action<ReadOnlyMemory<byte>>? onSourceChunk, Action<long>? progress, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
        try
        {
            await using var zstd = new CompressionStream(new NonClosingStream(output), level);
            if (workers > 1)
                zstd.SetParameter(ZSTD_cParameter.ZSTD_c_nbWorkers, workers);
            long position = NczFormat.UncompressedHeaderSize;
            while (remaining > 0)
            {
                ct.ThrowIfCancellationRequested();
                var want = (int)Math.Min(ChunkSize, remaining);
                await nca.ReadExactlyAsync(buffer.AsMemory(0, want), ct).ConfigureAwait(false);
                onSourceChunk?.Invoke(buffer.AsMemory(0, want));
                crypto.Apply(buffer.AsSpan(0, want), position);
                await zstd.WriteAsync(buffer.AsMemory(0, want), ct).ConfigureAwait(false);

                position += want;
                remaining -= want;
                progress?.Invoke(want);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task CompressBlocksAsync(
        Stream nca, Stream output, NczSectionCrypto crypto, long remaining, NczOptions options,
        Action<ReadOnlyMemory<byte>>? onSourceChunk, Action<long>? progress, CancellationToken ct)
    {
        if (!output.CanSeek)
            throw new NotSupportedException("Block-mode NCZ needs a seekable output stream.");

        var blockSize = 1 << options.BlockSizeExponent;
        var blockCount = (int)((remaining + blockSize - 1) / blockSize);

        var blockHeader = new byte[0x18];
        NczFormat.BlockMagic.CopyTo(blockHeader, 0);
        blockHeader[8] = 2;  // version
        blockHeader[9] = 1;  // type
        blockHeader[11] = (byte)options.BlockSizeExponent;
        BinaryPrimitives.WriteInt32LittleEndian(blockHeader.AsSpan(12), blockCount);
        BinaryPrimitives.WriteInt64LittleEndian(blockHeader.AsSpan(16), remaining);
        await output.WriteAsync(blockHeader, ct).ConfigureAwait(false);

        var sizeTablePosition = output.Position;
        var sizes = new byte[blockCount * 4];
        await output.WriteAsync(sizes, ct).ConfigureAwait(false);

        var parallel = Math.Max(1, options.MaxDegreeOfParallelism);
        var batch = new byte[parallel][];
        var batchLengths = new int[parallel];
        var results = new byte[parallel][];
        for (var i = 0; i < parallel; i++)
            batch[i] = new byte[blockSize];

        long position = NczFormat.UncompressedHeaderSize;
        var blockIndex = 0;
        using var compressors = new ThreadLocal<Compressor>(() => new Compressor(options.Level), trackAllValues: true);

        try
        {
            while (blockIndex < blockCount)
            {
                ct.ThrowIfCancellationRequested();
                var inBatch = Math.Min(parallel, blockCount - blockIndex);

                for (var i = 0; i < inBatch; i++)
                {
                    var len = (int)Math.Min(blockSize, remaining);
                    await nca.ReadExactlyAsync(batch[i].AsMemory(0, len), ct).ConfigureAwait(false);
                    onSourceChunk?.Invoke(batch[i].AsMemory(0, len));
                    crypto.Apply(batch[i].AsSpan(0, len), position);
                    batchLengths[i] = len;
                    position += len;
                    remaining -= len;
                }

                Parallel.For(0, inBatch, new ParallelOptions { MaxDegreeOfParallelism = parallel, CancellationToken = ct }, i =>
                {
                    var packed = compressors.Value!.Wrap(batch[i].AsSpan(0, batchLengths[i])).ToArray();
                    // Blocks that don't shrink are stored raw; readers detect this by size.
                    results[i] = packed.Length < batchLengths[i] ? packed : batch[i].AsSpan(0, batchLengths[i]).ToArray();
                });

                for (var i = 0; i < inBatch; i++)
                {
                    BinaryPrimitives.WriteInt32LittleEndian(sizes.AsSpan((blockIndex + i) * 4), results[i].Length);
                    await output.WriteAsync(results[i], ct).ConfigureAwait(false);
                    progress?.Invoke(batchLengths[i]);
                }
                blockIndex += inBatch;
            }
        }
        finally
        {
            foreach (var c in compressors.Values)
                c.Dispose();
        }

        var end = output.Position;
        output.Position = sizeTablePosition;
        await output.WriteAsync(sizes, ct).ConfigureAwait(false);
        output.Position = end;
    }
}
