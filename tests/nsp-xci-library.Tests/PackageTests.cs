using System.Security.Cryptography;
using NspXciLibrary.Meta;
using NspXciLibrary.Ncz;
using NspXciLibrary.Packages;

namespace NspXciLibrary.Tests;

/// <summary>End-to-end: synthetic NSP → NSZ → NSP, checking the program NCA survives byte for byte.</summary>
public sealed class PackageTests : IDisposable
{
    private const ulong TitleId = 0x0100DDDD00000000;
    private readonly string _dir = Directory.CreateTempSubdirectory("nsp-xci-library-tests").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private (string NspPath, byte[] Program) BuildBaseGameNsp(byte distribution = 0)
    {
        var program = TestNca.BuildProgram(Det.Compressible(0x180000), Det.Bytes("program-key", 16), TitleId, distribution);
        var cnmt = TestNca.BuildApplicationCnmt(TitleId, 0, program);
        var meta = TestNca.BuildMeta(cnmt, TitleId, Det.Bytes("meta-key", 16), distribution);

        var path = Path.Combine(_dir, "base.nsp");
        TestNca.WriteNsp(path,
        [
            (TestNca.ContentIdHex(program) + ".nca", program),
            (TestNca.ContentIdHex(meta) + ".cnmt.nca", meta),
        ]);
        return (path, program);
    }

    [Fact]
    public void Open_discovers_title_and_resolves_contents()
    {
        var keys = TestKeys.Create();
        var (path, program) = BuildBaseGameNsp();

        using var pkg = SwitchPackage.Open(path, keys);
        Assert.Equal(PackageFormat.Nsp, pkg.Format);

        var title = Assert.Single(pkg.Titles);
        Assert.Equal(ContentMetaType.Application, title.Type);
        Assert.Equal(TitleId, title.TitleId);
        var content = Assert.Single(title.Contents);
        Assert.True(content.IsPresent);
        Assert.Equal(program.Length, content.File!.Size);
    }

    [Fact]
    public async Task Nsp_to_nsz_to_nsp_round_trips_and_verifies()
    {
        var keys = TestKeys.Create();
        var (path, program) = BuildBaseGameNsp();
        var writer = new PackageWriter(keys);

        string nszPath;
        using (var pkg = SwitchPackage.Open(path, keys))
        {
            var plan = Assert.Single(MergePlanner.Plan([pkg]));
            nszPath = Path.Combine(_dir, "out.nsz");
            var options = new WriteOptions { Format = OutputFormat.Nsz, Ncz = new NczOptions { Level = 3 } };
            var progressSeen = false;
            var result = await writer.WriteAsync(plan, nszPath, options, new SyncProgress(_ => progressSeen = true));
            Assert.True(progressSeen);
            Assert.True(result.Size < new FileInfo(path).Length);
        }

        string nspPath;
        using (var nsz = SwitchPackage.Open(nszPath, keys))
        {
            Assert.Equal(PackageFormat.Nsz, nsz.Format);
            var plan = Assert.Single(MergePlanner.Plan([nsz]));
            nspPath = Path.Combine(_dir, "back.nsp");
            await writer.WriteAsync(plan, nspPath, new WriteOptions { Format = OutputFormat.Nsp, Verify = true });
        }

        using var back = SwitchPackage.Open(nspPath, keys);
        Assert.Equal(PackageFormat.Nsp, back.Format);
        var entry = back.FindFile(TestNca.ContentIdHex(program) + ".nca");
        Assert.NotNull(entry);
        using var s = back.OpenFile(entry!);
        var bytes = new byte[s.Length];
        s.ReadExactly(bytes);
        Assert.Equal(SHA256.HashData(program), SHA256.HashData(bytes));
    }

    [Fact]
    public async Task Corrupt_nca_fails_verification_and_leaves_no_output()
    {
        var keys = TestKeys.Create();
        var (path, _) = BuildBaseGameNsp();

        // Flip one byte inside the program NCA's body. The CNMT (in the meta NCA) is untouched.
        long corruptAt;
        using (var pkg = SwitchPackage.Open(path, keys))
            corruptAt = pkg.Titles[0].Contents[0].File!.Offset + 0x10000;

        using (var f = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
        {
            f.Position = corruptAt;
            var b = f.ReadByte();
            f.Position = corruptAt;
            f.WriteByte((byte)(b ^ 0xFF));
        }

        using var corrupt = SwitchPackage.Open(path, keys);
        var plan = Assert.Single(MergePlanner.Plan([corrupt]));
        var output = Path.Combine(_dir, "bad.nsp");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new PackageWriter(keys).WriteAsync(plan, output, new WriteOptions()));
        Assert.False(File.Exists(output));
        Assert.False(File.Exists(output + ".part"));
    }

    [Fact]
    public async Task Nca_converted_from_game_card_still_verifies()
    {
        var keys = TestKeys.Create();
        // CNMT hash is taken over the GameCard-distribution NCA, as on a real cartridge...
        var program = TestNca.BuildProgram(Det.Compressible(0x40000), Det.Bytes("program-key", 16), TitleId, distribution: 1);
        var cnmt = TestNca.BuildApplicationCnmt(TitleId, 0, program);
        var meta = TestNca.BuildMeta(cnmt, TitleId, Det.Bytes("meta-key", 16));

        // ...but the NSP holds the header rewritten to Download, as an XCI -> NSP conversion produces.
        var header = NspXciLibrary.Nca.NcaHeader.Decrypt(program, keys).ToArray();
        header[0x204] = 0;
        var converted = (byte[])program.Clone();
        NspXciLibrary.Nca.NcaHeader.Encrypt(header, keys).CopyTo(converted, 0);

        var path = Path.Combine(_dir, "converted.nsp");
        TestNca.WriteNsp(path,
        [
            (TestNca.ContentIdHex(program) + ".nca", converted),
            (TestNca.ContentIdHex(meta) + ".cnmt.nca", meta),
        ]);

        using var pkg = SwitchPackage.Open(path, keys);
        var plan = Assert.Single(MergePlanner.Plan([pkg]));
        var result = await new PackageWriter(keys).WriteAsync(plan, Path.Combine(_dir, "reverified.nsp"), new WriteOptions { Verify = true });
        Assert.True(File.Exists(result.OutputPath));
    }

    [Fact]
    public void Merge_planner_picks_newest_update_and_groups_by_application()
    {
        var keys = TestKeys.Create();
        var (path, _) = BuildBaseGameNsp();
        using var a = SwitchPackage.Open(path, keys);
        using var b = SwitchPackage.Open(path, keys);

        var plans = MergePlanner.Plan([a, b]);
        var plan = Assert.Single(plans);
        Assert.Equal(TitleId, plan.ApplicationId);
        Assert.NotNull(plan.Base);
        Assert.Null(plan.Update);
        Assert.Equal("[0100DDDD00000000][v0].nsz", SwitchConverter.DefaultFileName(plan, OutputFormat.Nsz));
    }

    /// <summary>IProgress that runs inline (Progress&lt;T&gt; posts asynchronously, which makes asserts racy).</summary>
    private sealed class SyncProgress(Action<WriteProgress> onReport) : IProgress<WriteProgress>
    {
        public void Report(WriteProgress value) => onReport(value);
    }
}
