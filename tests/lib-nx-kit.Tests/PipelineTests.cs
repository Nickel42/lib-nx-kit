using System.Security.Cryptography;
using LibNxKit.IO;
using LibNxKit.Nca;
using LibNxKit.Ncz;
using LibNxKit.Packages;

namespace LibNxKit.Tests;

/// <summary>
/// The overlapped (hash and write while the next chunk is read or decoded) and parallel-hash paths
/// must produce exactly what the serial ones do.
/// </summary>
public class PipelineTests
{
    private sealed record Observed(byte[] Output, byte[] ChunkHash, long Progress);

    private static async Task<Observed> Copy(byte[] data, bool overlap)
    {
        var output = new MemoryStream();
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long progress = 0;
        await StreamUtil.CopyAsync(new MemoryStream(data), output, data.Length, c => sha.AppendData(c.Span),
            n => progress += n, CancellationToken.None, overlap);
        return new Observed(output.ToArray(), sha.GetHashAndReset(), progress);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(StreamUtil.CopyBufferSize)]
    [InlineData(3 * StreamUtil.CopyBufferSize + 12345)]
    public async Task Overlapped_copy_matches_serial_copy(int length)
    {
        var data = Det.Bytes("copy", length);
        var serial = await Copy(data, overlap: false);
        var overlapped = await Copy(data, overlap: true);

        Assert.Equal(data, overlapped.Output);
        Assert.Equal(SHA256.HashData(data), overlapped.ChunkHash);
        Assert.Equal(serial.ChunkHash, overlapped.ChunkHash);
        Assert.Equal(length, overlapped.Progress);
    }

    [Fact]
    public async Task Overlapped_copy_surfaces_a_write_error()
    {
        var data = Det.Bytes("copy-fail", 3 * StreamUtil.CopyBufferSize);
        await Assert.ThrowsAsync<IOException>(() =>
            StreamUtil.CopyAsync(new MemoryStream(data), new FailingStream(), data.Length, null, null, CancellationToken.None, overlap: true));
    }

    [Theory]
    [InlineData(NczMode.Solid)]
    [InlineData(NczMode.Block)]
    public async Task Overlapped_ncz_decode_matches_serial_decode(NczMode mode)
    {
        var keys = TestKeys.Create();
        var nca = TestNca.BuildProgram(Det.Compressible(5 * 1024 * 1024 + 777), Det.Bytes("pipe-key", 16), 0x0100000000ABC000);
        var ncz = new MemoryStream();
        var source = new MemoryStream(nca);
        await NczCompressor.CompressAsync(source, new NcaReader(source, keys), ncz,
            new NczOptions { Level = 1, Mode = mode, BlockSizeExponent = 16 });

        foreach (var overlap in new[] { false, true })
        {
            ncz.Position = 0;
            var output = new MemoryStream();
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await NczDecompressor.DecompressAsync(ncz, output, nca.Length, c => sha.AppendData(c.Span), null, overlap, CancellationToken.None);
            Assert.Equal(nca, output.ToArray());
            Assert.Equal(SHA256.HashData(nca), sha.GetHashAndReset());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Parallel_hasher_matches_both_forms(bool parallel)
    {
        var data = Det.Bytes("hasher", 2 * 1024 * 1024 + 99);
        var header = Det.Bytes("alt-header", 0xC00);
        var withHeader = (byte[])data.Clone();
        header.CopyTo(withHeader, 0);

        bool Check(byte[] expected)
        {
            using var hasher = new PackageWriter.NcaHasher(header, parallel: parallel);
            // A chunk ending inside the substituted header, one crossing its end, then chunks large
            // enough to be hashed on two threads.
            var offset = 0;
            foreach (var size in new[] { 0x100, 0x1000, 1024 * 1024 })
            {
                hasher.Append(data.AsMemory(offset, size));
                offset += size;
            }
            hasher.Append(data.AsMemory(offset));
            return hasher.Matches(expected);
        }

        Assert.True(Check(SHA256.HashData(data)));
        Assert.True(Check(SHA256.HashData(withHeader)));
        Assert.False(Check(SHA256.HashData(Det.Bytes("other", 16))));
    }

    private sealed class FailingStream : Stream
    {
        private int _writes;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (++_writes == 2)
                throw new IOException("disk full");
        }
    }
}
