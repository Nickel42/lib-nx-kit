using System.Security.Cryptography;
using LibNxKit.Containers;
using LibNxKit.Crypto;
using LibNxKit.IO;
using LibNxKit.Keys;
using LibNxKit.Meta;
using LibNxKit.Nca;
using LibNxKit.Ncz;

namespace LibNxKit.Packages;

public enum OutputFormat
{
    Nsp,
    Nsz,
    Xci,
    Xcz,
}

public static class OutputFormatExtensions
{
    public static bool IsCompressed(this OutputFormat format) => format is OutputFormat.Nsz or OutputFormat.Xcz;
    public static bool IsGameCard(this OutputFormat format) => format is OutputFormat.Xci or OutputFormat.Xcz;

    public static string Extension(this OutputFormat format) => format switch
    {
        OutputFormat.Nsp => ".nsp",
        OutputFormat.Nsz => ".nsz",
        OutputFormat.Xci => ".xci",
        OutputFormat.Xcz => ".xcz",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };
}

public sealed record WriteOptions
{
    public OutputFormat Format { get; init; } = OutputFormat.Nsp;

    public NczOptions Ncz { get; init; } = new();

    /// <summary>Check each NCA's SHA-256 against its CNMT entry while writing. Adds little cost.</summary>
    public bool Verify { get; init; } = true;

    /// <summary>Keep update delta fragments. They are only used to patch an installed older update; usually dropped.</summary>
    public bool IncludeDeltaFragments { get; init; }

    /// <summary>
    /// When writing NSP/NSZ from XCI/XCZ, rewrite game-card NCA headers to the download distribution
    /// type so the console will install them. The NCA body is unchanged.
    /// </summary>
    public bool ConvertGameCardToDownload { get; init; } = true;
}

public readonly record struct WriteProgress(long BytesDone, long BytesTotal, string CurrentFile)
{
    public double Fraction => BytesTotal == 0 ? 0 : (double)BytesDone / BytesTotal;
}

public sealed record WriteResult(string OutputPath, long Size, IReadOnlyList<string> Warnings);

/// <summary>Writes titles (from one or many packages) into a single NSP, NSZ, XCI or XCZ.</summary>
public sealed class PackageWriter
{
    private enum ItemKind { Copy, Decompress, Compress }

    private sealed record Item(
        SwitchPackage Package,
        PartitionEntry Source,
        string OutputName,
        ItemKind Kind,
        long EstimatedSize,
        byte[]? ExpectedHash,
        bool IsNca,
        bool ToDownload,
        byte[]? StripRightsWithKey,
        ulong TitleId);

    private readonly KeySet _keys;

    public PackageWriter(KeySet keys) => _keys = keys;

    public Task<WriteResult> WriteAsync(MergePlan plan, string outputPath, WriteOptions options,
        IProgress<WriteProgress>? progress = null, CancellationToken ct = default)
        => WriteAsync(plan.Titles, outputPath, options, progress, ct, plan.Warnings);

    public async Task<WriteResult> WriteAsync(
        IEnumerable<PackageTitle> titles,
        string outputPath,
        WriteOptions options,
        IProgress<WriteProgress>? progress = null,
        CancellationToken ct = default,
        IReadOnlyList<string>? initialWarnings = null)
    {
        var warnings = new List<string>(initialWarnings ?? Array.Empty<string>());
        var titleList = titles.ToList();
        var items = BuildItems(titleList, options, warnings);
        if (items.Count == 0)
            throw new InvalidOperationException("Nothing to write.");

        var tracker = new ProgressTracker(items.Sum(i => i.EstimatedSize), progress);
        var tempPath = outputPath + ".part";

        try
        {
            await using (var output = new FileStream(tempPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None,
                             1 << 20, FileOptions.Asynchronous))
            {
                if (options.Format.IsGameCard())
                {
                    var packageId = titleList.Select(t => t.ApplicationId).FirstOrDefault();
                    await WriteGameCardAsync(items, output, options, packageId, tracker, warnings, ct).ConfigureAwait(false);
                }
                else
                {
                    await WritePartitionFsAsync(items, output, options, tracker, warnings, ct).ConfigureAwait(false);
                }
                await output.FlushAsync(ct).ConfigureAwait(false);
            }

            File.Move(tempPath, outputPath, overwrite: true);
            return new WriteResult(outputPath, new FileInfo(outputPath).Length, warnings);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    // ---------------------------------------------------------------- layouts

    private async Task WritePartitionFsAsync(List<Item> items, Stream output, WriteOptions options,
        ProgressTracker tracker, List<string> warnings, CancellationToken ct)
    {
        // Header length depends only on file names, so a placeholder can be patched in place later.
        var placeholder = Pfs0.BuildHeader(items.Select(i => (i.OutputName, 0L)).ToList());
        await output.WriteAsync(placeholder, ct).ConfigureAwait(false);

        var sizes = new long[items.Count];
        for (var i = 0; i < items.Count; i++)
        {
            var start = output.Position;
            await WriteItemAsync(items[i], output, options, tracker, warnings, ct).ConfigureAwait(false);
            sizes[i] = output.Position - start;
        }

        var header = Pfs0.BuildHeader(items.Select((it, idx) => (it.OutputName, sizes[idx])).ToList());
        if (header.Length != placeholder.Length)
            throw new InvalidOperationException("PFS0 header size changed unexpectedly.");
        output.Position = 0;
        await output.WriteAsync(header, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Card layout: header at 0, root HFS0 at 0xF000 holding empty "update" and "normal" partitions
    /// and a "secure" partition with the NCAs. Files are 0x200-aligned. Headers are written as
    /// placeholders and filled in once sizes and hashes are known.
    /// </summary>
    private async Task WriteGameCardAsync(List<Item> items, Stream output, WriteOptions options, ulong packageId,
        ProgressTracker tracker, List<string> warnings, CancellationToken ct)
    {
        var empty = Hfs0.BuildHeader([]);
        var emptyHash = SHA256.HashData(empty);
        string[] rootNames = ["update", "normal", "secure"];

        var rootPlaceholder = Hfs0.BuildHeader(rootNames.Select(n => new Hfs0BuildEntry(n, 0, 0, 0, new byte[32])).ToList());
        var securePlaceholder = Hfs0.BuildHeader(items.Select(i => new Hfs0BuildEntry(i.OutputName, 0, 0, 0, new byte[32])).ToList());

        var rootOffset = Xci.RootPartitionOffset;
        var secureOffset = rootOffset + rootPlaceholder.Length + 2 * empty.Length;
        var secureDataStart = secureOffset + securePlaceholder.Length;

        output.Position = 0;
        await output.WriteAsync(new byte[rootOffset], ct).ConfigureAwait(false);
        await output.WriteAsync(rootPlaceholder, ct).ConfigureAwait(false);
        await output.WriteAsync(empty, ct).ConfigureAwait(false);
        await output.WriteAsync(empty, ct).ConfigureAwait(false);
        await output.WriteAsync(securePlaceholder, ct).ConfigureAwait(false);

        var entries = new List<Hfs0BuildEntry>(items.Count);
        foreach (var item in items)
        {
            var start = output.Position;
            await WriteItemAsync(item, output, options, tracker, warnings, ct).ConfigureAwait(false);
            var size = output.Position - start;

            var hashed = (int)Math.Min(0x200, size);
            var firstBlock = new byte[hashed];
            output.Position = start;
            await output.ReadExactlyAsync(firstBlock, ct).ConfigureAwait(false);
            entries.Add(new Hfs0BuildEntry(item.OutputName, start - secureDataStart, size, hashed, SHA256.HashData(firstBlock)));

            output.Position = start + size;
            await PadToAsync(output, StreamUtil.AlignUp(output.Position, 0x200), ct).ConfigureAwait(false);
        }

        var totalSize = output.Position;
        var secureHeader = Hfs0.BuildHeader(entries);
        var root = Hfs0.BuildHeader(
        [
            new Hfs0BuildEntry("update", 0, empty.Length, empty.Length, emptyHash),
            new Hfs0BuildEntry("normal", empty.Length, empty.Length, empty.Length, emptyHash),
            new Hfs0BuildEntry("secure", 2L * empty.Length, totalSize - secureOffset, secureHeader.Length, SHA256.HashData(secureHeader)),
        ]);
        if (secureHeader.Length != securePlaceholder.Length || root.Length != rootPlaceholder.Length)
            throw new InvalidOperationException("HFS0 header size changed unexpectedly.");

        output.Position = secureOffset;
        await output.WriteAsync(secureHeader, ct).ConfigureAwait(false);
        output.Position = rootOffset;
        await output.WriteAsync(root, ct).ConfigureAwait(false);
        output.Position = 0;
        await output.WriteAsync(Xci.BuildHeader(rootOffset, root, secureOffset, totalSize, packageId), ct).ConfigureAwait(false);
        output.Position = totalSize;
    }

    private static async Task PadToAsync(Stream output, long target, CancellationToken ct)
    {
        var gap = target - output.Position;
        if (gap > 0)
            await output.WriteAsync(new byte[gap], ct).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- planning

    private List<Item> BuildItems(IReadOnlyList<PackageTitle> titles, WriteOptions options, List<string> warnings)
    {
        var items = new List<Item>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var gameCardOut = options.Format.IsGameCard();
        var compressOut = options.Format.IsCompressed();

        void Add(Item item)
        {
            if (names.Add(item.OutputName))
                items.Add(item);
        }

        foreach (var title in titles)
        {
            var pkg = title.Package;
            var toDownload = !gameCardOut && options.ConvertGameCardToDownload && pkg.IsGameCard;

            var metaIsNcz = title.MetaFile.Name.EndsWith(".ncz", StringComparison.OrdinalIgnoreCase);
            var metaKind = metaIsNcz ? ItemKind.Decompress : ItemKind.Copy;
            Add(new Item(pkg, title.MetaFile, Path.ChangeExtension(title.MetaFile.Name, ".nca"), metaKind,
                EstimateSize(pkg, title.MetaFile, metaKind), null, IsNca: true, toDownload, null, title.TitleId));

            foreach (var content in title.Contents)
            {
                var type = content.Record.Type;
                if (type == CnmtContentType.Meta)
                    continue;
                if (type == CnmtContentType.DeltaFragment && !options.IncludeDeltaFragments)
                    continue;
                if (content.File is null)
                {
                    if (type != CnmtContentType.DeltaFragment)
                        throw new InvalidDataException($"{title}: content {content.Record.ContentIdHex} is missing.");
                    warnings.Add($"{title}: delta fragment {content.Record.ContentIdHex} not present; skipped.");
                    continue;
                }

                var isNcz = content.IsCompressed;
                var kind = (compressOut, isNcz) switch
                {
                    (false, false) => ItemKind.Copy,
                    (false, true) => ItemKind.Decompress,
                    (true, true) => ItemKind.Copy,
                    (true, false) => ShouldCompress(pkg, content.File, warnings) ? ItemKind.Compress : ItemKind.Copy,
                };

                var producesNcz = kind == ItemKind.Compress || (kind == ItemKind.Copy && isNcz);
                var name = content.Record.ContentIdHex + (producesNcz ? ".ncz" : ".nca");

                // An NCZ copied as-is can't be hashed against the CNMT (the hash is of the NCA).
                var hash = options.Verify && !(kind == ItemKind.Copy && isNcz) ? content.Record.Hash : null;

                // Game cards carry no tickets, so title-key content is converted to standard crypto.
                byte[]? stripKey = null;
                if (gameCardOut)
                {
                    var reader = pkg.OpenNca(content.File);
                    if (reader.Header.HasRightsId)
                        stripKey = reader.GetContentKey();
                }

                Add(new Item(pkg, content.File, name, kind, EstimateSize(pkg, content.File, kind), hash,
                    IsNca: true, toDownload, stripKey, title.TitleId));
            }

            if (gameCardOut)
                continue;

            // Tickets and certificates for rights-ID content. The rights ID begins with the title ID.
            var prefix = title.TitleId.ToString("X16");
            foreach (var (rightsId, ticket) in pkg.Tickets)
            {
                if (!rightsId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                Add(new Item(pkg, ticket.TicketFile, ticket.TicketFile.Name, ItemKind.Copy, ticket.TicketFile.Size,
                    null, IsNca: false, false, null, title.TitleId));
                if (ticket.CertFile is not null)
                    Add(new Item(pkg, ticket.CertFile, ticket.CertFile.Name, ItemKind.Copy, ticket.CertFile.Size,
                        null, IsNca: false, false, null, title.TitleId));
                else
                    warnings.Add($"Ticket {rightsId} has no certificate file.");
            }
        }
        return items;
    }

    private static long EstimateSize(SwitchPackage pkg, PartitionEntry entry, ItemKind kind)
        => kind == ItemKind.Decompress ? pkg.OpenNca(entry).Header.ContentSize : entry.Size;

    /// <summary>
    /// Like nsz, compress only Program and PublicData NCAs (the bulk of every title). NCAs whose key
    /// is unavailable are stored as-is rather than failing the whole write.
    /// </summary>
    private static bool ShouldCompress(SwitchPackage pkg, PartitionEntry file, List<string> warnings)
    {
        if (file.Size <= NczFormat.UncompressedHeaderSize * 4)
            return false;

        var reader = pkg.OpenNca(file);
        if (reader.Header.ContentType is not (NcaContentType.Program or NcaContentType.PublicData))
            return false;

        try
        {
            reader.GetContentKey();
            return true;
        }
        catch (Exception ex) when (ex is MissingKeyException or NotSupportedException)
        {
            warnings.Add($"{file.Name} stored uncompressed: {ex.Message}");
            return false;
        }
    }

    // ---------------------------------------------------------------- per-file

    private async Task WriteItemAsync(Item item, Stream output, WriteOptions options, ProgressTracker tracker,
        List<string> warnings, CancellationToken ct)
    {
        var start = output.Position;
        var sourceHeader = item.IsNca ? item.Package.OpenNca(item.Source).Header : null;

        using var sha = item.ExpectedHash is null ? null : new NcaHasher(AlternateGameCardHeader(sourceHeader));
        Action<ReadOnlyMemory<byte>>? hashChunk = sha is null ? null : new Action<ReadOnlyMemory<byte>>(sha.Append);
        Action<long> report = n => tracker.Advance(n, item.OutputName);

        using (var source = item.Package.OpenFile(item.Source))
        {
            switch (item.Kind)
            {
                case ItemKind.Copy:
                    source.Position = 0;
                    await StreamUtil.CopyAsync(source, output, source.Length, hashChunk, report, ct).ConfigureAwait(false);
                    break;

                case ItemKind.Decompress:
                    source.Position = 0;
                    await NczDecompressor.DecompressAsync(source, output, sourceHeader!.ContentSize, hashChunk, report, ct)
                        .ConfigureAwait(false);
                    break;

                case ItemKind.Compress:
                {
                    var reader = new NcaReader(source, _keys, item.Package.LookupTicket);
                    await NczCompressor.CompressAsync(source, reader, output, options.Ncz, hashChunk, report, ct).ConfigureAwait(false);
                    break;
                }
            }
        }

        if (sha is not null && !sha.Matches(item.ExpectedHash!))
        {
            // Title-key NCAs converted to standard crypto (for an XCI, by this library or other tools) no
            // longer match their CNMT hash. Rebuild the original header and check again — this only
            // re-reads the file on the rare mismatch path.
            if (sourceHeader is not null && await MatchesOriginalTitleKeyFormAsync(item, sourceHeader, ct).ConfigureAwait(false))
            {
                warnings.Add($"{item.Source.Name} was converted from title-key crypto; verified against its original form.");
            }
            else if (item.Package.IsGameCard && sourceHeader?.DistributionType == NcaDistributionType.Download)
            {
                // Converted by a tool that changed the header in a way we can't reconstruct.
                warnings.Add($"{item.Source.Name} can't be verified: it was converted to game-card form by another tool.");
            }
            else
            {
                throw new InvalidDataException(
                    $"{item.Source.Name} failed verification: its SHA-256 does not match the CNMT. The source file is corrupt. " +
                    "(If it was converted by another tool, turn off WriteOptions.Verify.)");
            }
        }

        if (item.IsNca && (item.ToDownload || item.StripRightsWithKey is not null))
            await PatchHeaderAsync(output, start, item, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Rewrites the NCA header written at <paramref name="ncaStart"/> (NCAs and NCZs both start with it):
    /// distribution type → Download, and/or rights ID removed with the title key moved into the key area.
    /// </summary>
    private async Task PatchHeaderAsync(Stream output, long ncaStart, Item item, CancellationToken ct)
    {
        var end = output.Position;
        var encrypted = new byte[NcaHeader.Size];
        output.Position = ncaStart;
        await output.ReadExactlyAsync(encrypted, ct).ConfigureAwait(false);

        var header = NcaHeader.Decrypt(encrypted, _keys);
        var raw = header.ToArray();
        var changed = false;

        if (item.ToDownload && header.DistributionType == NcaDistributionType.GameCard)
        {
            raw[0x204] = (byte)NcaDistributionType.Download;
            changed = true;
        }

        if (item.StripRightsWithKey is { } titleKey && header.HasRightsId)
        {
            var keyArea = new byte[0x40];
            titleKey.CopyTo(keyArea, 0x20); // slot 2: the AES-CTR key
            raw[0x207] = (byte)KeyAreaKeyKind.Application;
            AesEcb.Encrypt(_keys.GetKeyAreaKey(KeyAreaKeyKind.Application, header.MasterKeyRevision), keyArea)
                .CopyTo(raw, 0x300);
            Array.Clear(raw, 0x230, 0x10);
            changed = true;
        }

        if (changed)
        {
            output.Position = ncaStart;
            await output.WriteAsync(NcaHeader.Encrypt(raw, _keys), ct).ConfigureAwait(false);
        }
        output.Position = end;
    }

    /// <summary>
    /// Re-hashes the source NCA with candidate reconstructions of its pre-conversion header: rights ID
    /// restored (title ID, big-endian, then zeros and the key generation), key area cleared, key-area
    /// index 0, with either distribution type. Returns true if any candidate matches the CNMT hash.
    /// </summary>
    private async Task<bool> MatchesOriginalTitleKeyFormAsync(Item item, NcaHeader header, CancellationToken ct)
    {
        if (header.HasRightsId)
            return false;

        var current = header.ToArray();
        var candidates = new List<byte[]>();
        var seen = new HashSet<string>();
        foreach (var titleId in new[] { item.TitleId, header.ProgramId }.Distinct())
        foreach (var last in new[] { header.KeyGeneration, (byte)header.MasterKeyRevision }.Distinct())
        foreach (var distribution in new byte[] { current[0x204], (byte)(current[0x204] ^ 1) })
        {
            var raw = (byte[])current.Clone();
            raw[0x204] = distribution;
            raw[0x207] = (byte)KeyAreaKeyKind.Application;
            Array.Clear(raw, 0x300, 0x40);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(raw.AsSpan(0x230), titleId);
            Array.Clear(raw, 0x238, 8);
            raw[0x23F] = last;
            if (seen.Add(Convert.ToHexString(raw)))
                candidates.Add(NcaHeader.Encrypt(raw, _keys));
        }

        var hashers = candidates.Select(c => new NcaHasher(c, alternateOnly: true)).ToList();
        try
        {
            void Feed(ReadOnlyMemory<byte> chunk)
            {
                foreach (var h in hashers)
                    h.Append(chunk);
            }

            using var source = item.Package.OpenFile(item.Source);
            source.Position = 0;
            if (item.Source.Name.EndsWith(".ncz", StringComparison.OrdinalIgnoreCase))
                await NczDecompressor.DecompressAsync(source, Stream.Null, header.ContentSize, Feed, null, ct).ConfigureAwait(false);
            else
                await StreamUtil.CopyAsync(source, Stream.Null, source.Length, Feed, null, ct).ConfigureAwait(false);

            return hashers.Any(h => h.MatchesAlternate(item.ExpectedHash!));
        }
        finally
        {
            foreach (var h in hashers)
                h.Dispose();
        }
    }

    /// <summary>
    /// An NCA converted from a game card (by this library, 4NXCI or NSC_Builder) has a Download header,
    /// but its CNMT hash is of the original GameCard header. Returns that original encrypted header so
    /// verification can accept either form; null when not applicable.
    /// </summary>
    private byte[]? AlternateGameCardHeader(NcaHeader? header)
    {
        if (header is null || header.DistributionType != NcaDistributionType.Download)
            return null;
        var raw = header.ToArray();
        raw[0x204] = (byte)NcaDistributionType.GameCard;
        return NcaHeader.Encrypt(raw, _keys);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ---------------------------------------------------------------- helpers

    private sealed class ProgressTracker(long total, IProgress<WriteProgress>? progress)
    {
        private long _done;

        public void Advance(long bytes, string file)
        {
            _done += bytes;
            progress?.Report(new WriteProgress(Math.Min(_done, total), total, file));
        }
    }

    /// <summary>SHA-256 over an NCA as it streams, optionally also over a variant with a substituted header.</summary>
    private sealed class NcaHasher : IDisposable
    {
        private readonly IncrementalHash? _primary;
        private readonly IncrementalHash? _alternate;
        private readonly byte[]? _alternateHeader;
        private long _position;

        /// <param name="alternateOnly">Skip the hash of the bytes as-is (used when re-checking candidates).</param>
        public NcaHasher(byte[]? alternateHeader, bool alternateOnly = false)
        {
            if (!alternateOnly)
                _primary = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            _alternateHeader = alternateHeader;
            if (alternateHeader is not null)
                _alternate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        }

        public void Append(ReadOnlyMemory<byte> chunk)
        {
            var span = chunk.Span;
            _primary?.AppendData(span);

            if (_alternate is not null)
            {
                var headerLeft = (int)Math.Max(0, Math.Min(_alternateHeader!.Length - _position, span.Length));
                if (headerLeft > 0)
                    _alternate.AppendData(_alternateHeader.AsSpan((int)_position, headerLeft));
                _alternate.AppendData(span[headerLeft..]);
            }
            _position += span.Length;
        }

        public bool Matches(byte[] expected)
            => (_primary is not null && _primary.GetHashAndReset().AsSpan().SequenceEqual(expected)) | MatchesAlternate(expected);

        public bool MatchesAlternate(byte[] expected)
            => _alternate is not null && _alternate.GetHashAndReset().AsSpan().SequenceEqual(expected);

        public void Dispose()
        {
            _primary?.Dispose();
            _alternate?.Dispose();
        }
    }
}
