using System.Buffers.Binary;
using System.Security.Cryptography;
using LibNxKit.Containers;
using LibNxKit.Packages;
using Xunit;

namespace LibNxKit.Tests;

/// <summary>
/// <see cref="WriteOptions.KeepSourceLayout"/>: an NSP compressed to NSZ and back is the original file byte for byte,
/// whatever order its files were in and however its header was padded.
/// </summary>
public sealed class SourceLayoutTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("lib-nx-kit-layout").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string RoundTrip(string nsp, WriteOptions? toNsz = null, WriteOptions? toNsp = null)
    {
        var converter = new SwitchConverter(TestKeys.Create());
        var nszDir = Path.Combine(_dir, "nsz-" + Guid.NewGuid().ToString("N")[..6]);
        var nspDir = Path.Combine(_dir, "nsp-" + Guid.NewGuid().ToString("N")[..6]);
        var nsz = converter.ConvertAsync(nsp, nszDir, toNsz ?? new WriteOptions { Format = OutputFormat.Nsz }).GetAwaiter().GetResult();
        var back = converter.ConvertAsync(Assert.Single(nsz.Outputs).OutputPath, nspDir, toNsp ?? new WriteOptions { Format = OutputFormat.Nsp })
            .GetAwaiter().GetResult();
        return Assert.Single(back.Outputs).OutputPath;
    }

    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static List<string> Names(string path)
    {
        using var fs = File.OpenRead(path);
        return Pfs0.Read(fs).Select(e => e.Name).ToList();
    }

    [Fact]
    public void BaseGame_ComesBackByteForByte()
    {
        var nsp = Path.Combine(_dir, "base.nsp");
        TestTitles.WriteNsp(nsp, TestTitles.Base()); // contents first, meta last
        var back = RoundTrip(nsp);
        Assert.Equal(Names(nsp), Names(back));
        Assert.Equal(Sha(nsp), Sha(back));
    }

    [Fact]
    public void UpdateWithTicketAndCertificate_ComesBackByteForByte()
    {
        var nsp = Path.Combine(_dir, "update.nsp");
        TestTitles.WriteNsp(nsp, TestTitles.Update());
        Assert.Equal(Sha(nsp), Sha(RoundTrip(nsp)));
    }

    [Fact]
    public void TheNszKeepsTheSourceOrderToo()
    {
        var nsp = Path.Combine(_dir, "order.nsp");
        TestTitles.WriteNsp(nsp, TestTitles.Base());
        var nsz = new SwitchConverter(TestKeys.Create())
            .ConvertAsync(nsp, Path.Combine(_dir, "nsz"), new WriteOptions { Format = OutputFormat.Nsz }).GetAwaiter().GetResult();
        // Only program and data NCAs are compressed (.ncz); the control and meta NCAs stay .nca. The order is the same.
        static string Id(string name) => name[..name.IndexOf('.')];
        Assert.Equal(Names(nsp).Select(Id), Names(Assert.Single(nsz.Outputs).OutputPath).Select(Id));
    }

    [Fact]
    public void AnotherWritersHeaderPadding_IsKept()
    {
        // The same files, with the header padded to 0x200 as some dumping tools do.
        var files = TestTitles.Base().Files;
        var normal = Pfs0.BuildHeader(files.Select(f => (f.Name, (long)f.Data.Length)).ToList());
        var count = files.Count;
        var padded = Pfs0.BuildHeader(files.Select(f => (f.Name, (long)f.Data.Length)).ToList(),
            (int)(TestNca.Align(normal.Length, 0x200) - 0x10 - count * 0x18));
        Assert.NotEqual(normal.Length, padded.Length);
        var nsp = Path.Combine(_dir, "padded.nsp");
        using (var f = File.Create(nsp))
        {
            f.Write(padded);
            foreach (var (_, data) in files) f.Write(data);
        }
        Assert.Equal(0x200, BinaryPrimitives.ReadInt32LittleEndian(File.ReadAllBytes(nsp).AsSpan(8)) + 0x10 + count * 0x18);
        Assert.Equal(Sha(nsp), Sha(RoundTrip(nsp)));
    }

    [Fact]
    public void WithoutIt_EachTitleIsWrittenMetaFirst()
    {
        var nsp = Path.Combine(_dir, "meta-first.nsp");
        TestTitles.WriteNsp(nsp, TestTitles.Base());
        var off = new WriteOptions { Format = OutputFormat.Nsp, KeepSourceLayout = false };
        var report = new SwitchConverter(TestKeys.Create()).ConvertAsync(nsp, Path.Combine(_dir, "off"), off).GetAwaiter().GetResult();
        var names = Names(Assert.Single(report.Outputs).OutputPath);
        Assert.EndsWith(".cnmt.nca", names[0]);
        Assert.NotEqual(Names(nsp), names);
    }
}
