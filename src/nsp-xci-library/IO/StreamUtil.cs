using System.Buffers;
using System.Text;

namespace NspXciLibrary.IO;

internal static class StreamUtil
{
    public const int CopyBufferSize = 4 * 1024 * 1024;

    public static byte[] ReadAt(Stream stream, long offset, int count)
    {
        var buffer = new byte[count];
        stream.Position = offset;
        stream.ReadExactly(buffer);
        return buffer;
    }

    public static string ReadAsciiZ(ReadOnlySpan<byte> table, int offset)
    {
        if (offset < 0 || offset >= table.Length)
            throw new InvalidDataException("File name offset is outside the string table.");
        var end = table[offset..].IndexOf((byte)0);
        var slice = end < 0 ? table[offset..] : table.Slice(offset, end);
        return Encoding.UTF8.GetString(slice);
    }

    /// <summary>
    /// Copies exactly <paramref name="length"/> bytes, invoking <paramref name="onChunk"/> with each
    /// chunk (for hashing) and <paramref name="progress"/> with the byte count.
    /// </summary>
    public static async Task CopyAsync(
        Stream source,
        Stream destination,
        long length,
        Action<ReadOnlyMemory<byte>>? onChunk,
        Action<long>? progress,
        CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            var remaining = length;
            while (remaining > 0)
            {
                ct.ThrowIfCancellationRequested();
                var want = (int)Math.Min(buffer.Length, remaining);
                var read = await source.ReadAsync(buffer.AsMemory(0, want), ct).ConfigureAwait(false);
                if (read == 0)
                    throw new EndOfStreamException($"Source ended with {remaining} bytes still expected.");

                var chunk = buffer.AsMemory(0, read);
                onChunk?.Invoke(chunk);
                await destination.WriteAsync(chunk, ct).ConfigureAwait(false);
                remaining -= read;
                progress?.Invoke(read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static long AlignUp(long value, long alignment) => (value + alignment - 1) / alignment * alignment;
}
