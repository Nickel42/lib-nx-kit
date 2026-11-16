using System.Buffers;

namespace LibNxKit.IO;

/// <summary>
/// Takes filled buffers and, for each, calls the chunk observer (hashing), writes it and reports
/// progress. With overlap on, that runs on a worker while the caller fills the next buffer: two
/// pooled buffers alternate, and chunks are still consumed one at a time in order, so the hash and
/// the written bytes are exactly those of a serial loop. With overlap off, each chunk is consumed
/// before <see cref="SubmitAsync"/> returns.
/// </summary>
internal sealed class OverlappedChunkWriter : IAsyncDisposable
{
    private readonly Stream _destination;
    private readonly Action<ReadOnlyMemory<byte>>? _onChunk;
    private readonly Action<long>? _progress;
    private readonly bool _overlap;
    private readonly CancellationToken _ct;
    private readonly byte[][] _buffers;
    private int _current;
    private Task _pending = Task.CompletedTask;

    public OverlappedChunkWriter(Stream destination, int bufferSize, Action<ReadOnlyMemory<byte>>? onChunk,
        Action<long>? progress, bool overlap, CancellationToken ct)
    {
        _destination = destination;
        _onChunk = onChunk;
        _progress = progress;
        _overlap = overlap;
        _ct = ct;
        _buffers = overlap
            ? [ArrayPool<byte>.Shared.Rent(bufferSize), ArrayPool<byte>.Shared.Rent(bufferSize)]
            : [ArrayPool<byte>.Shared.Rent(bufferSize)];
    }

    /// <summary>The buffer to fill next (at least the requested size). Read it again after every submit.</summary>
    public byte[] Buffer => _buffers[_current];

    /// <summary>Hands over the first <paramref name="count"/> bytes of <see cref="Buffer"/>.</summary>
    public async Task SubmitAsync(int count)
    {
        var chunk = _buffers[_current].AsMemory(0, count);
        if (!_overlap)
        {
            await ConsumeAsync(chunk).ConfigureAwait(false);
            return;
        }

        // The previous chunk owns the other buffer until it has been written; this also surfaces its errors.
        await _pending.ConfigureAwait(false);
        _pending = Task.Run(() => ConsumeAsync(chunk));
        _current ^= 1;
    }

    /// <summary>Waits until every submitted chunk has been written.</summary>
    public async Task FlushAsync()
    {
        var pending = _pending;
        _pending = Task.CompletedTask;
        await pending.ConfigureAwait(false);
    }

    private async Task ConsumeAsync(ReadOnlyMemory<byte> chunk)
    {
        _onChunk?.Invoke(chunk);
        await _destination.WriteAsync(chunk, _ct).ConfigureAwait(false);
        _progress?.Invoke(chunk.Length);
    }

    public async ValueTask DisposeAsync()
    {
        // Never return a buffer to the pool while a write may still be reading it. A failure here was
        // either already surfaced by FlushAsync or the caller is unwinding from its own error.
        try { await _pending.ConfigureAwait(false); }
        catch { /* see above */ }
        foreach (var buffer in _buffers)
            ArrayPool<byte>.Shared.Return(buffer);
    }
}
