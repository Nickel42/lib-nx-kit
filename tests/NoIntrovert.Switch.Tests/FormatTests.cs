using NoIntrovert.Switch.Containers;
using NoIntrovert.Switch.Meta;
using NoIntrovert.Switch.Nca;
using NoIntrovert.Switch.Ncz;

namespace NoIntrovert.Switch.Tests;

public class FormatTests
{
    [Fact]
    public void Pfs0_round_trips_names_offsets_and_sizes()
    {
        var files = new List<(string Name, long Size)> { ("a.nca", 10), ("bb.tik", 0x2C0), ("ccc.cert", 0x700) };
        var header = Pfs0.BuildHeader(files);
        Assert.Equal(0, header.Length % 0x20);

        using var ms = new MemoryStream();
        ms.Write(header);
        ms.Write(new byte[files.Sum(f => f.Size)]);

        var entries = Pfs0.Read(ms);
        Assert.Equal(files.Select(f => f.Name), entries.Select(e => e.Name));
        Assert.Equal(files.Select(f => f.Size), entries.Select(e => e.Size));
        Assert.Equal(header.Length, entries[0].Offset);
        Assert.Equal(header.Length + 10, entries[1].Offset);
    }

    [Fact]
    public void Nca_header_decrypts_and_content_key_comes_from_key_area()
    {
        var keys = TestKeys.Create();
        var contentKey = Det.Bytes("content-key", 16);
        var body = Det.Compressible(0x30000);
        var nca = TestNca.BuildProgram(body, contentKey, 0x0100AAAA00000000);

        using var ms = new MemoryStream(nca);
        var reader = new NcaReader(ms, keys);

        Assert.Equal(NcaContentType.Program, reader.Header.ContentType);
        Assert.Equal(nca.Length, reader.Header.ContentSize);
        Assert.Equal(0x0100AAAA00000000UL, reader.Header.ProgramId);
        Assert.False(reader.Header.HasRightsId);
        Assert.Equal(contentKey, reader.GetContentKey());

        var section = reader.Header.Sections.Single();
        Assert.Equal(TestNca.SectionStart, section.Offset);

        using var plain = reader.OpenSection(section);
        var read = new byte[body.Length];
        plain.ReadExactly(read);
        Assert.Equal(body, read);
    }

    [Fact]
    public void Header_encrypt_is_inverse_of_decrypt()
    {
        var keys = TestKeys.Create();
        var nca = TestNca.BuildProgram(new byte[0x200], Det.Bytes("k", 16), 1);
        var header = NcaHeader.Decrypt(nca, keys);
        Assert.Equal(nca.AsSpan(0, NcaHeader.Size).ToArray(), NcaHeader.Encrypt(header.ToArray(), keys));
    }

    [Theory]
    [InlineData(NczMode.Solid)]
    [InlineData(NczMode.Block)]
    public async Task Ncz_compress_then_decompress_restores_identical_nca(NczMode mode)
    {
        var keys = TestKeys.Create();
        var nca = TestNca.BuildProgram(Det.Compressible(0x250000), Det.Bytes("content-key", 16), 0x0100BBBB00000000);

        using var source = new MemoryStream(nca);
        var reader = new NcaReader(source, keys);
        using var ncz = new MemoryStream();
        var options = new NczOptions { Level = 3, Mode = mode, BlockSizeExponent = 16, MaxDegreeOfParallelism = 4 };
        await NczCompressor.CompressAsync(source, reader, ncz, options);

        Assert.True(ncz.Length < nca.Length / 4, $"expected real compression, got {ncz.Length} of {nca.Length}");

        ncz.Position = 0;
        using var restored = new MemoryStream();
        await NczDecompressor.DecompressAsync(ncz, restored, nca.Length);
        Assert.Equal(nca, restored.ToArray());
    }

    [Fact]
    public void Cnmt_parses_application_record()
    {
        var program = TestNca.BuildProgram(new byte[0x400], Det.Bytes("k", 16), 0x0100CCCC00000000);
        var cnmt = Cnmt.Parse(TestNca.BuildApplicationCnmt(0x0100CCCC00000000, 0, program));

        Assert.Equal(ContentMetaType.Application, cnmt.Type);
        Assert.Equal(0x0100CCCC00000000UL, cnmt.ApplicationId);
        Assert.Equal(0x0100CCCC00000800UL, cnmt.RelatedTitleId);
        var content = cnmt.Contents.Single();
        Assert.Equal(CnmtContentType.Program, content.Type);
        Assert.Equal(program.Length, content.Size);
        Assert.Equal(TestNca.ContentIdHex(program), content.ContentIdHex);
    }
}
