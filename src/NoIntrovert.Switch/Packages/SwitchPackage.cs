using NoIntrovert.Switch.Containers;
using NoIntrovert.Switch.IO;
using NoIntrovert.Switch.Keys;
using NoIntrovert.Switch.Meta;
using NoIntrovert.Switch.Nca;

namespace NoIntrovert.Switch.Packages;

public enum PackageFormat
{
    Nsp,
    Nsz,
    Xci,
    Xcz,
}

/// <summary>A content file referenced by a title's CNMT, resolved to its file in the package (if present).</summary>
public sealed record PackageContent(CnmtContent Record, PartitionEntry? File)
{
    public bool IsPresent => File is not null;
    public bool IsCompressed => File?.Name.EndsWith(".ncz", StringComparison.OrdinalIgnoreCase) == true;
}

/// <summary>One title (base game, update or DLC) found inside a package.</summary>
public sealed class PackageTitle
{
    internal PackageTitle(SwitchPackage package, Cnmt meta, PartitionEntry metaFile, IReadOnlyList<PackageContent> contents)
    {
        Package = package;
        Meta = meta;
        MetaFile = metaFile;
        Contents = contents;
    }

    public SwitchPackage Package { get; }
    public Cnmt Meta { get; }
    public PartitionEntry MetaFile { get; }
    public IReadOnlyList<PackageContent> Contents { get; }

    public ulong TitleId => Meta.TitleId;
    public ulong ApplicationId => Meta.ApplicationId;
    public uint Version => Meta.Version;
    public ContentMetaType Type => Meta.Type;

    private Nacp? _nacp;
    private bool _nacpLoaded;

    /// <summary>
    /// The title's control data (name, publisher, display version), read from its Control NCA.
    /// Null for DLC (which has no control data), when the Control NCA is compressed, or when it
    /// can't be decrypted. Read once and cached.
    /// </summary>
    public Nacp? GetControlData()
    {
        if (!_nacpLoaded)
        {
            _nacp = Package.TryReadNacp(this);
            _nacpLoaded = true;
        }
        return _nacp;
    }

    public override string ToString() => $"{Type} {TitleId:X16} v{Version}";
}

/// <summary>A ticket and its certificate chain shipped in a package.</summary>
public sealed record PackageTicket(Ticket Ticket, PartitionEntry TicketFile, PartitionEntry? CertFile);

/// <summary>
/// An opened NSP, NSZ, XCI or XCZ. Keeps the file open until disposed.
/// Not thread-safe: read one file at a time.
/// </summary>
public sealed class SwitchPackage : IDisposable
{
    private readonly Stream _stream;
    private readonly KeySet _keys;

    private SwitchPackage(string path, IReadOnlyList<string> parts, Stream stream, KeySet keys, PackageFormat format,
        IReadOnlyList<PartitionEntry> files)
    {
        Path = path;
        Parts = parts;
        _stream = stream;
        _keys = keys;
        Format = format;
        Files = files;
        Tickets = LoadTickets();
        Titles = LoadTitles();
    }

    /// <summary>The path the package was opened from (a file, a first split part, or a folder of parts).</summary>
    public string Path { get; }

    /// <summary>The files that make up the package; more than one for split packages.</summary>
    public IReadOnlyList<string> Parts { get; }

    public PackageFormat Format { get; }

    /// <summary>All files in the package (PFS0 root for NSP/NSZ, secure partition for XCI/XCZ).</summary>
    public IReadOnlyList<PartitionEntry> Files { get; }

    /// <summary>Tickets keyed by rights ID (upper-case hex).</summary>
    public IReadOnlyDictionary<string, PackageTicket> Tickets { get; }

    public IReadOnlyList<PackageTitle> Titles { get; }

    public bool IsGameCard => Format is PackageFormat.Xci or PackageFormat.Xcz;

    /// <summary>
    /// Opens a package. <paramref name="path"/> may be an ordinary file, the first part of a split
    /// package (*.ns0, *.xc0, *.00) or a folder of parts named 00, 01, ….
    /// </summary>
    public static SwitchPackage Open(string path, KeySet keys)
    {
        var parts = ConcatStream.ResolveParts(path);
        var streams = new List<Stream>();
        Stream stream;
        try
        {
            foreach (var part in parts)
                streams.Add(new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan));
            stream = streams.Count == 1 ? streams[0] : new ConcatStream(streams);
        }
        catch
        {
            foreach (var s in streams)
                s.Dispose();
            throw;
        }

        try
        {
            IReadOnlyList<PartitionEntry> files;
            bool gameCard;
            if (Pfs0.IsPfs0(stream))
            {
                files = Pfs0.Read(stream);
                gameCard = false;
            }
            else if (Xci.FindHeaderBase(stream) >= 0)
            {
                files = Xci.ReadSecurePartition(stream);
                gameCard = true;
            }
            else
            {
                throw new InvalidDataException($"'{System.IO.Path.GetFileName(path)}' is not an NSP, NSZ, XCI or XCZ file.");
            }

            var compressed = files.Any(f => f.Name.EndsWith(".ncz", StringComparison.OrdinalIgnoreCase));
            var format = (gameCard, compressed) switch
            {
                (false, false) => PackageFormat.Nsp,
                (false, true) => PackageFormat.Nsz,
                (true, false) => PackageFormat.Xci,
                (true, true) => PackageFormat.Xcz,
            };

            return new SwitchPackage(path, parts, stream, keys, format, files);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Opens a read-only view of one file in the package. Dispose it before opening another for reading.</summary>
    public Stream OpenFile(PartitionEntry entry) => new SubStream(_stream, entry.Offset, entry.Size);

    public PartitionEntry? FindFile(string name)
        => Files.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

    internal Ticket? LookupTicket(string rightsIdHex)
        => Tickets.TryGetValue(rightsIdHex, out var t) ? t.Ticket : null;

    /// <summary>Opens an NCA (or the NCA header of an NCZ) for header/section access.</summary>
    public NcaReader OpenNca(PartitionEntry entry) => new(OpenFile(entry), _keys, LookupTicket);

    private Dictionary<string, PackageTicket> LoadTickets()
    {
        var result = new Dictionary<string, PackageTicket>(StringComparer.OrdinalIgnoreCase);
        foreach (var tik in Files.Where(f => f.Name.EndsWith(".tik", StringComparison.OrdinalIgnoreCase)))
        {
            Ticket ticket;
            try
            {
                using var s = OpenFile(tik);
                var bytes = new byte[Math.Min(tik.Size, 0x400)];
                s.ReadExactly(bytes);
                ticket = Ticket.Parse(bytes);
            }
            catch (InvalidDataException)
            {
                continue; // malformed ticket: content that needs it will fail with a clear key error later
            }

            var certName = System.IO.Path.ChangeExtension(tik.Name, ".cert");
            result[ticket.RightsIdHex] = new PackageTicket(ticket, tik, FindFile(certName));
        }
        return result;
    }

    private List<PackageTitle> LoadTitles()
    {
        var ncas = Files.Where(f => f.Name.EndsWith(".nca", StringComparison.OrdinalIgnoreCase)).ToList();
        var metaCandidates = ncas.Where(f => f.Name.EndsWith(".cnmt.nca", StringComparison.OrdinalIgnoreCase)).ToList();
        if (metaCandidates.Count == 0)
            metaCandidates = ncas; // some dumps don't use the .cnmt.nca naming; check headers instead

        var byContentId = new Dictionary<string, PartitionEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Files)
        {
            var dot = f.Name.IndexOf('.');
            var stem = dot < 0 ? f.Name : f.Name[..dot];
            if (stem.Length == 32 && (f.Name.EndsWith(".nca", StringComparison.OrdinalIgnoreCase)
                                      || f.Name.EndsWith(".ncz", StringComparison.OrdinalIgnoreCase)))
                byContentId.TryAdd(stem, f);
        }

        var titles = new List<PackageTitle>();
        foreach (var entry in metaCandidates)
        {
            using var file = OpenFile(entry);
            NcaReader reader;
            try
            {
                reader = new NcaReader(file, _keys, LookupTicket);
            }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException && metaCandidates == ncas)
            {
                continue; // header scan fallback: skip anything that isn't a readable NCA3
            }

            if (reader.Header.ContentType != NcaContentType.Meta || reader.Header.Sections.Count == 0)
                continue;

            var cnmt = ReadCnmt(reader);
            var contents = cnmt.Contents
                .Select(c => new PackageContent(c, byContentId.GetValueOrDefault(c.ContentIdHex)))
                .ToList();
            titles.Add(new PackageTitle(this, cnmt, entry, contents));
        }
        return titles;
    }

    internal Nacp? TryReadNacp(PackageTitle title)
    {
        var control = title.Contents.FirstOrDefault(c => c.Record.Type == CnmtContentType.Control && c.IsPresent && !c.IsCompressed);
        if (control is null)
            return null;

        try
        {
            using var file = OpenFile(control.File!);
            var reader = new NcaReader(file, _keys, LookupTicket);
            var section = reader.Header.Sections.FirstOrDefault(s => s.FsType == NcaFsType.RomFs);
            if (section is null)
                return null;

            using var stream = reader.OpenSection(section);
            var romFsOffset = section.HashType == NcaHashType.HierarchicalIntegrity ? section.IntegrityDataLevel.Offset : 0;
            var nacpFile = RomFs.ListFiles(stream, romFsOffset)
                .FirstOrDefault(f => string.Equals(f.Name, "control.nacp", StringComparison.OrdinalIgnoreCase));
            if (nacpFile is null || nacpFile.Size < Nacp.Size)
                return null;

            return Nacp.Parse(StreamUtil.ReadAt(stream, nacpFile.Offset, Nacp.Size));
        }
        catch (Exception ex) when (ex is InvalidDataException or MissingKeyException or NotSupportedException
                                       or EndOfStreamException or ArgumentException)
        {
            return null; // naming is a convenience; never fail a conversion over it
        }
    }

    private static Cnmt ReadCnmt(NcaReader reader)
    {
        var (section, pfsOffset) = reader.OpenPartitionFs(reader.Header.Sections[0]);
        using (section)
        {
            var files = Pfs0.Read(section, pfsOffset);
            var cnmtFile = files.FirstOrDefault(f => f.Name.EndsWith(".cnmt", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("Meta NCA does not contain a .cnmt file.");
            var bytes = StreamUtil.ReadAt(section, cnmtFile.Offset, checked((int)cnmtFile.Size));
            return Cnmt.Parse(bytes);
        }
    }

    public void Dispose() => _stream.Dispose();
}
