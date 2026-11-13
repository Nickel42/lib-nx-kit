using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using LibNxKit.Containers;
using LibNxKit.Crypto;
using LibNxKit.Keys;

namespace LibNxKit.Tests;

/// <summary>Deterministic test data, identical to the Python reference script's det(label, n).</summary>
internal static class Det
{
    public static byte[] Bytes(string label, int length)
    {
        var output = new byte[length];
        var written = 0;
        for (var i = 0; written < length; i++)
        {
            var block = SHA256.HashData(Encoding.UTF8.GetBytes($"{label}{i}"));
            var take = Math.Min(block.Length, length - written);
            block.AsSpan(0, take).CopyTo(output.AsSpan(written));
            written += take;
        }
        return output;
    }

    /// <summary>Compressible but non-trivial data, so zstd actually has something to do.</summary>
    public static byte[] Compressible(int length, int seed = 0)
    {
        var data = new byte[length];
        var phrase = Encoding.ASCII.GetBytes($"lib-nx-kit test payload {seed} ");
        for (var i = 0; i < length; i++)
            data[i] = (byte)(phrase[i % phrase.Length] ^ (i / 4096 & 0x7));
        return data;
    }
}

/// <summary>Fake console keys. Real keys are never needed for the tests.</summary>
internal static class TestKeys
{
    public static readonly byte[] HeaderKey = Det.Bytes("test-header-key", 0x20);
    public static readonly byte[] KeyAreaKey = Det.Bytes("test-kak-app-00", 0x10);
    public static readonly byte[] TitleKek = Det.Bytes("test-titlekek-00", 0x10);

    public static KeySet Create() => KeySet.FromText(
        $"""
        header_key = {Convert.ToHexString(HeaderKey)}
        key_area_key_application_00 = {Convert.ToHexString(KeyAreaKey)}
        titlekek_00 = {Convert.ToHexString(TitleKek)}
        """);
}

/// <summary>NCA content types (header byte 0x205).</summary>
internal enum TestContentType : byte { Program = 0, Meta = 1, Control = 2, PublicData = 5 }

/// <summary>Builds small but structurally valid NCA3 files encrypted with <see cref="TestKeys"/>.</summary>
internal static class TestNca
{
    public const int SectionStart = 0x4000;

    /// <summary>A Program (or PublicData) NCA with one AES-CTR section holding <paramref name="body"/>.</summary>
    /// <param name="rightsId">When set, the NCA uses title-key crypto: <paramref name="contentKey"/> is the title key.</param>
    public static byte[] BuildProgram(byte[] body, byte[] contentKey, ulong programId, byte distribution = 0,
        byte[]? rightsId = null, TestContentType type = TestContentType.Program)
        => Build(body, contentKey, programId, type, fsType: 0, hashType: 3, hashData: null, distribution, rightsId);

    /// <summary>A Meta NCA whose section 0 is a hashed PFS0 containing one .cnmt file.</summary>
    public static byte[] BuildMeta(byte[] cnmt, ulong titleId, byte[] contentKey, byte distribution = 0)
    {
        var name = $"Application_{titleId:x16}.cnmt";
        var pfsHeader = Pfs0.BuildHeader([(name, (long)cnmt.Length)]);
        // Fake 0x200-byte hash table, then the PFS0 (layer 1).
        var section = new byte[0x200 + pfsHeader.Length + cnmt.Length];
        pfsHeader.CopyTo(section, 0x200);
        cnmt.CopyTo(section, 0x200 + pfsHeader.Length);

        var hashData = new byte[0xF8];
        BinaryPrimitives.WriteInt64LittleEndian(hashData.AsSpan(0x38), 0x200);
        BinaryPrimitives.WriteInt64LittleEndian(hashData.AsSpan(0x40), section.Length - 0x200);
        return Build(section, contentKey, titleId, TestContentType.Meta, fsType: 1, hashType: 2, hashData, distribution, null);
    }

    /// <summary>A Control NCA whose RomFS (behind a fake IVFC header) holds a control.nacp.</summary>
    public static byte[] BuildControl(string gameName, string displayVersion, ulong titleId, byte[] contentKey,
        byte[]? rightsId = null, byte distribution = 0)
    {
        var nacp = new byte[0x4000];
        Encoding.UTF8.GetBytes(gameName).CopyTo(nacp, 0x0);          // AmericanEnglish name
        Encoding.UTF8.GetBytes("Test Publisher").CopyTo(nacp, 0x200); // AmericanEnglish publisher
        Encoding.UTF8.GetBytes(displayVersion).CopyTo(nacp, 0x3060);

        // RomFS: 0x50 header, one file entry at 0x50, file data at 0x200.
        var fileName = Encoding.UTF8.GetBytes("control.nacp");
        var entrySize = 0x20 + (fileName.Length + 3) / 4 * 4;
        var romfs = new byte[0x200 + nacp.Length];
        var r = romfs.AsSpan();
        BinaryPrimitives.WriteInt64LittleEndian(r, 0x50);
        BinaryPrimitives.WriteInt64LittleEndian(r[(7 * 8)..], 0x50);       // file meta offset
        BinaryPrimitives.WriteInt64LittleEndian(r[(8 * 8)..], entrySize);  // file meta size
        BinaryPrimitives.WriteInt64LittleEndian(r[(9 * 8)..], 0x200);      // data offset
        var entry = r.Slice(0x50, entrySize);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[0x4..], 0xFFFFFFFF); // sibling: none
        BinaryPrimitives.WriteInt64LittleEndian(entry[0x8..], 0);           // data offset
        BinaryPrimitives.WriteInt64LittleEndian(entry[0x10..], nacp.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[0x18..], 0xFFFFFFFF); // hash chain: none
        BinaryPrimitives.WriteInt32LittleEndian(entry[0x1C..], fileName.Length);
        fileName.CopyTo(entry[0x20..]);
        nacp.CopyTo(romfs, 0x200);

        // Section: 0x400 bytes standing in for the hash levels, then the RomFS (IVFC level 6).
        const int romFsOffset = 0x400;
        var section = new byte[romFsOffset + romfs.Length];
        romfs.CopyTo(section, romFsOffset);

        var ivfc = new byte[0xF8];
        var v = ivfc.AsSpan();
        Encoding.ASCII.GetBytes("IVFC").CopyTo(v);
        BinaryPrimitives.WriteUInt32LittleEndian(v[0x4..], 0x20000);
        BinaryPrimitives.WriteUInt32LittleEndian(v[0x8..], 0x20);
        BinaryPrimitives.WriteInt32LittleEndian(v[0xC..], 7);
        var level6 = v.Slice(0x10 + 5 * 0x18, 0x18);
        BinaryPrimitives.WriteInt64LittleEndian(level6, romFsOffset);
        BinaryPrimitives.WriteInt64LittleEndian(level6[8..], romfs.Length);
        BinaryPrimitives.WriteInt32LittleEndian(level6[0x10..], 14);

        return Build(section, contentKey, titleId, TestContentType.Control, fsType: 0, hashType: 3, ivfc, distribution, rightsId);
    }

    private static byte[] Build(byte[] sectionData, byte[] contentKey, ulong programId, TestContentType contentType,
        byte fsType, byte hashType, byte[]? hashData, byte distribution, byte[]? rightsId)
    {
        var sectionSize = (int)Align(sectionData.Length, 0x200);
        var nca = new byte[SectionStart + sectionSize];
        var header = new byte[0xC00];
        var h = header.AsSpan();

        Encoding.ASCII.GetBytes("NCA3").CopyTo(h[0x200..]);
        h[0x204] = distribution;
        h[0x205] = (byte)contentType;
        h[0x206] = 0; // key generation (old)
        h[0x207] = 0; // key area key index: application
        BinaryPrimitives.WriteInt64LittleEndian(h[0x208..], nca.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(h[0x210..], programId);
        h[0x220] = 0; // key generation

        BinaryPrimitives.WriteUInt32LittleEndian(h[0x240..], SectionStart / 0x200);
        BinaryPrimitives.WriteUInt32LittleEndian(h[0x244..], (uint)((SectionStart + sectionSize) / 0x200));

        if (rightsId is not null)
        {
            rightsId.CopyTo(header, 0x230); // title-key crypto: key area unused (left zero)
        }
        else
        {
            // Key area: slot 2 is the AES-CTR key; the area is encrypted with the key-area key.
            var keyArea = new byte[0x40];
            contentKey.CopyTo(keyArea, 0x20);
            AesEcb.Encrypt(TestKeys.KeyAreaKey, keyArea).CopyTo(header, 0x300);
        }

        // FS header 0.
        var fs = h.Slice(0x400, 0x200);
        BinaryPrimitives.WriteUInt16LittleEndian(fs, 2);
        fs[0x2] = fsType;
        fs[0x3] = hashType;
        fs[0x4] = 3; // AesCtr
        hashData?.CopyTo(fs[0x8..]);
        var nonceLe = Det.Bytes($"nonce{programId}{(byte)contentType}", 8);
        nonceLe.CopyTo(fs[0x140..]);

        // Body: plaintext, then CTR-encrypted with the big-endian form of the nonce.
        sectionData.CopyTo(nca, SectionStart);
        var counter = new byte[16];
        for (var j = 0; j < 8; j++)
            counter[j] = nonceLe[7 - j];
        using (var ctr = new AesCtrCipher(contentKey, counter))
            ctr.Transform(nca.AsSpan(SectionStart, sectionSize), SectionStart);

        using (var xts = new NintendoAesXts(TestKeys.HeaderKey))
            xts.Encrypt(header, 0);
        header.CopyTo(nca, 0);
        return nca;
    }

    /// <summary>Builds a CNMT for an Application (0x80), Patch (0x81) or AddOnContent (0x82).</summary>
    public static byte[] BuildCnmt(ulong titleId, uint version, byte metaType, ulong relatedTitleId,
        IReadOnlyList<(byte[] Nca, byte CnmtType)> contents)
    {
        var extSize = metaType switch { 0x80 => 0x10, 0x81 => 0x18, _ => 0x10 };
        var cnmt = new byte[0x20 + extSize + contents.Count * 0x38];
        var c = cnmt.AsSpan();
        BinaryPrimitives.WriteUInt64LittleEndian(c, titleId);
        BinaryPrimitives.WriteUInt32LittleEndian(c[0x8..], version);
        c[0xC] = metaType;
        BinaryPrimitives.WriteUInt16LittleEndian(c[0xE..], (ushort)extSize);
        BinaryPrimitives.WriteUInt16LittleEndian(c[0x10..], (ushort)contents.Count);
        BinaryPrimitives.WriteUInt64LittleEndian(c[0x20..], relatedTitleId);

        for (var i = 0; i < contents.Count; i++)
        {
            var (nca, cnmtType) = contents[i];
            var hash = SHA256.HashData(nca);
            var r = c.Slice(0x20 + extSize + i * 0x38, 0x38);
            hash.CopyTo(r);
            hash.AsSpan(0, 0x10).CopyTo(r[0x20..]);
            var size = (long)nca.Length;
            for (var b = 0; b < 6; b++)
                r[0x30 + b] = (byte)(size >> (8 * b));
            r[0x36] = cnmtType;
        }
        return cnmt;
    }

    /// <summary>Builds a minimal Application CNMT referencing one Program NCA.</summary>
    public static byte[] BuildApplicationCnmt(ulong titleId, uint version, byte[] programNca)
        => BuildCnmt(titleId, version, 0x80, titleId | 0x800, [(programNca, 1)]);

    /// <summary>A rights ID as the eShop forms it: title ID (big-endian), zeros, key generation.</summary>
    public static byte[] RightsIdFor(ulong titleId)
    {
        var rightsId = new byte[0x10];
        BinaryPrimitives.WriteUInt64BigEndian(rightsId, titleId);
        return rightsId;
    }

    /// <summary>A common (non-personalized) RSA-2048-SHA256 ticket carrying the encrypted title key.</summary>
    public static byte[] BuildTicket(byte[] rightsId, byte[] titleKey)
    {
        var ticket = new byte[0x2C0];
        BinaryPrimitives.WriteUInt32LittleEndian(ticket, 0x10004);
        var body = ticket.AsSpan(0x140);
        AesEcb.Encrypt(TestKeys.TitleKek, titleKey).CopyTo(body[0x40..]);
        body[0x141] = 0; // common
        body[0x145] = 0; // master key revision
        rightsId.CopyTo(body[0x160..]);
        return ticket;
    }

    public static string ContentIdHex(byte[] nca) => Convert.ToHexString(SHA256.HashData(nca).AsSpan(0, 0x10)).ToLowerInvariant();

    public static long Align(long value, long alignment) => (value + alignment - 1) / alignment * alignment;

    /// <summary>Writes an NSP (PFS0) containing the given files.</summary>
    public static void WriteNsp(string path, IReadOnlyList<(string Name, byte[] Data)> files)
    {
        using var f = File.Create(path);
        f.Write(Pfs0.BuildHeader(files.Select(x => (x.Name, (long)x.Data.Length)).ToList()));
        foreach (var (_, data) in files)
            f.Write(data);
    }
}

/// <summary>A synthetic title: its NCAs plus the file names they'd have in an NSP.</summary>
internal sealed record TestTitle(ulong TitleId, IReadOnlyList<(string Name, byte[] Data)> Files, byte[] MainNca);

/// <summary>Builds whole synthetic titles (base game, update, DLC) with consistent CNMTs.</summary>
internal static class TestTitles
{
    public const ulong BaseId = 0x0100EEEE00000000;
    public const ulong UpdateId = BaseId | 0x800;
    public const ulong DlcId = BaseId + 0x1001;

    /// <param name="distribution">1 (GameCard) for a title as a cartridge holds it; 0 (Download) as the eShop sells it.</param>
    public static TestTitle Base(string name = "Test Game", byte distribution = 0)
    {
        var program = TestNca.BuildProgram(Det.Compressible(0x60000, 1), Det.Bytes("base-program", 16), BaseId, distribution);
        var control = TestNca.BuildControl(name, "1.0.0", BaseId, Det.Bytes("base-control", 16), distribution: distribution);
        var cnmt = TestNca.BuildCnmt(BaseId, 0, 0x80, UpdateId, [(program, 1), (control, 3)]);
        return Assemble(BaseId, cnmt, program, [program, control], distribution);
    }

    /// <summary>An update whose NCAs use title-key crypto, shipped with its ticket and certificate.</summary>
    public static TestTitle Update(uint version = 0x10000, string name = "Test Game", string displayVersion = "1.1.0")
    {
        var rightsId = TestNca.RightsIdFor(UpdateId);
        var titleKey = Det.Bytes($"update-titlekey-{version}", 16);
        var program = TestNca.BuildProgram(Det.Compressible(0x50000, 2), titleKey, UpdateId, rightsId: rightsId);
        var control = TestNca.BuildControl(name, displayVersion, UpdateId, titleKey, rightsId);
        var cnmt = TestNca.BuildCnmt(UpdateId, version, 0x81, BaseId, [(program, 1), (control, 3)]);
        var title = Assemble(UpdateId, cnmt, program, [program, control]);

        var rightsHex = Convert.ToHexString(rightsId).ToLowerInvariant();
        var files = title.Files.ToList();
        files.Add((rightsHex + ".tik", TestNca.BuildTicket(rightsId, titleKey)));
        files.Add((rightsHex + ".cert", new byte[0x700]));
        return title with { Files = files };
    }

    /// <param name="seed">Changes the DLC's data (not its ID or version), as a differently dumped copy would.</param>
    /// <param name="distribution">1 (GameCard) for DLC as a cartridge holds it; 0 (Download) as the eShop sells it.</param>
    public static TestTitle Dlc(int seed = 3, byte distribution = 0)
    {
        var data = TestNca.BuildProgram(Det.Compressible(0x40000, seed), Det.Bytes("dlc-data", 16), DlcId, distribution,
            type: TestContentType.PublicData);
        var cnmt = TestNca.BuildCnmt(DlcId, 0, 0x82, BaseId, [(data, 2)]);
        return Assemble(DlcId, cnmt, data, [data], distribution);
    }

    private static TestTitle Assemble(ulong titleId, byte[] cnmt, byte[] main, IReadOnlyList<byte[]> ncas, byte distribution = 0)
    {
        var meta = TestNca.BuildMeta(cnmt, titleId, Det.Bytes($"meta-{titleId:x16}-{cnmt.Length}-{cnmt[8]}", 16), distribution);
        var files = ncas.Select(n => (TestNca.ContentIdHex(n) + ".nca", n)).ToList();
        files.Add((TestNca.ContentIdHex(meta) + ".cnmt.nca", meta));
        return new TestTitle(titleId, files, main);
    }

    public static void WriteNsp(string path, params TestTitle[] titles)
        => TestNca.WriteNsp(path, titles.SelectMany(t => t.Files).ToList());
}
