using System.Security.Cryptography;
using LibNxKit.Meta;
using LibNxKit.Nca;
using LibNxKit.Ncz;
using LibNxKit.Packages;

namespace LibNxKit.Tests;

/// <summary>Where a conversion starts: an eShop package in each format, or a cartridge dump (GameCard NCAs) as XCI/XCZ.</summary>
public enum MatrixSource { Nsp, Nsz, Xci, Xcz, CartridgeXci, CartridgeXcz }

/// <summary>
/// Every source format converted to every output format (NSP, NSZ, XCI, XCZ), each holding a base game, an update
/// with a ticket and a DLC. Each output is opened and checked for its format, titles, tickets, NCA headers, and
/// that every NCA still decrypts to the original data. Also: block-mode NCZ, byte-exact round trips and a chain
/// through all four formats.
/// </summary>
public sealed class ConversionMatrixTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("lib-nx-kit-matrix").FullName;
    private readonly SwitchConverter _converter = new(TestKeys.Create());

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static readonly OutputFormat[] AllFormats = [OutputFormat.Nsp, OutputFormat.Nsz, OutputFormat.Xci, OutputFormat.Xcz];

    public static TheoryData<MatrixSource, OutputFormat> EveryConversion()
    {
        var data = new TheoryData<MatrixSource, OutputFormat>();
        foreach (var source in Enum.GetValues<MatrixSource>())
            foreach (var target in AllFormats)
                data.Add(source, target);
        return data;
    }

    // ------------------------------------------------------------------- helpers

    private static WriteOptions Options(OutputFormat format, NczMode mode = NczMode.Solid) => new()
    {
        Format = format,
        Verify = true,
        Ncz = new NczOptions { Level = 3, Mode = mode, BlockSizeExponent = 16 },
    };

    /// <summary>
    /// What a package has been through, which decides what its conversions must look like.
    /// </summary>
    /// <param name="CartridgeOrigin">Its base game and DLC came from a cartridge, so their CNMTs hash GameCard headers.</param>
    /// <param name="PassedThroughCard">It has been a game card, so the update needs no ticket and has none.</param>
    /// <param name="GameCardHeaders">Its base game and DLC NCAs still have GameCard headers.</param>
    private sealed record Lineage(bool CartridgeOrigin, bool PassedThroughCard, bool GameCardHeaders)
    {
        public static Lineage Of(MatrixSource source) => source switch
        {
            MatrixSource.Nsp or MatrixSource.Nsz => new(false, false, false),
            MatrixSource.Xci or MatrixSource.Xcz => new(false, true, false),
            _ => new(true, true, true),
        };

        /// <summary>NSP/NSZ output rewrites GameCard headers to Download; game-card output keeps them and drops tickets.</summary>
        public Lineage Then(OutputFormat target) =>
            new(CartridgeOrigin, PassedThroughCard || target.IsGameCard(), GameCardHeaders && target.IsGameCard());
    }

    private static PackageFormat ToPackageFormat(OutputFormat format) => format switch
    {
        OutputFormat.Nsp => PackageFormat.Nsp,
        OutputFormat.Nsz => PackageFormat.Nsz,
        OutputFormat.Xci => PackageFormat.Xci,
        OutputFormat.Xcz => PackageFormat.Xcz,
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    /// <summary>Converts one file into a fresh folder and returns the single output's path.</summary>
    private async Task<string> Convert(string input, OutputFormat format, NczMode mode = NczMode.Solid)
    {
        var folder = Path.Combine(_dir, $"{format}-{Guid.NewGuid():N}"[..16].ToLowerInvariant());
        var report = await _converter.ConvertAsync(input, folder, Options(format, mode));
        Assert.Empty(report.Skipped);
        var output = Assert.Single(report.Outputs);
        Assert.DoesNotContain(output.Warnings, w => w.Contains("can't be verified"));
        return output.OutputPath;
    }

    /// <summary>The eShop package everything starts from: base game, update (title-key crypto, ticket) and DLC.</summary>
    private string EShopNsp()
    {
        var path = Path.Combine(_dir, "eshop.nsp");
        if (!File.Exists(path))
            TestTitles.WriteNsp(path, TestTitles.Base(), TestTitles.Update(), TestTitles.Dlc());
        return path;
    }

    /// <summary>The titles a cartridge holds: base game and DLC with GameCard-distribution NCAs, which changes their hashes.</summary>
    private string CartridgeNsp()
    {
        var path = Path.Combine(_dir, "cartridge-contents.nsp");
        if (!File.Exists(path))
            TestTitles.WriteNsp(path, TestTitles.Base(distribution: 1), TestTitles.Update(), TestTitles.Dlc(distribution: 1));
        return path;
    }

    /// <summary>Builds the source file in the requested format.</summary>
    private async Task<string> BuildSource(MatrixSource source)
    {
        switch (source)
        {
            case MatrixSource.Nsp: return EShopNsp();
            case MatrixSource.Nsz: return await Convert(EShopNsp(), OutputFormat.Nsz);
            case MatrixSource.Xci: return await Convert(EShopNsp(), OutputFormat.Xci);
            case MatrixSource.Xcz: return await Convert(EShopNsp(), OutputFormat.Xcz);
        }

        // A cartridge: base game and DLC with GameCard-distribution NCAs (their CNMT hashes taken over those
        // headers), plus an update, which the XCI writer turns from title-key to key-area crypto as carts store it.
        var xci = await Convert(CartridgeNsp(), OutputFormat.Xci);
        using (var pkg = SwitchPackage.Open(xci, TestKeys.Create()))
        {
            var baseProgram = pkg.Titles.Single(t => t.Type == ContentMetaType.Application)
                .Contents.Single(c => c.Record.Type == CnmtContentType.Program).File!;
            Assert.Equal(NcaDistributionType.GameCard, pkg.OpenNca(baseProgram).Header.DistributionType);
        }
        return source == MatrixSource.CartridgeXci ? xci : await Convert(xci, OutputFormat.Xcz);
    }

    private static byte[] ReadAll(Stream s)
    {
        var bytes = new byte[s.Length];
        s.ReadExactly(bytes);
        return bytes;
    }

    private static string Sha(string path) => System.Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    /// <summary>For each title and content: SHA-256 of the decrypted first section. Needs uncompressed NCAs.</summary>
    private static Dictionary<(ulong TitleId, CnmtContentType Type), string> PlaintextHashes(SwitchPackage pkg)
    {
        var result = new Dictionary<(ulong, CnmtContentType), string>();
        foreach (var title in pkg.Titles)
            foreach (var content in title.Contents)
            {
                Assert.True(content.IsPresent, $"{title} is missing its {content.Record.Type} NCA");
                Assert.False(content.IsCompressed);
                var reader = pkg.OpenNca(content.File!);
                using var section = reader.OpenSection(reader.Header.Sections[0]);
                result.Add((title.TitleId, content.Record.Type), System.Convert.ToHexString(SHA256.HashData(ReadAll(section))));
            }
        return result;
    }

    private Dictionary<(ulong, CnmtContentType), string> ExpectedPlaintext()
    {
        using var original = SwitchPackage.Open(EShopNsp(), TestKeys.Create());
        return PlaintextHashes(original);
    }

    private static List<(ulong TitleId, ContentMetaType Type, uint Version, string ContentIds)> TitleSummary(SwitchPackage pkg) =>
        pkg.Titles
            .Select(t => (t.TitleId, t.Type, t.Version, string.Join(",", t.Meta.Contents.Select(c => c.ContentIdHex))))
            .OrderBy(t => t.TitleId).ToList();

    /// <summary>
    /// Opens a converted package and checks everything a conversion of a package with this <paramref name="lineage"/>
    /// to <paramref name="target"/> promises.
    /// </summary>
    private Task AssertConverted(string outputPath, MatrixSource source, OutputFormat target)
        => AssertConverted(outputPath, Lineage.Of(source), target);

    private async Task AssertConverted(string outputPath, Lineage lineage, OutputFormat target)
    {
        var after = lineage.Then(target);
        var keys = TestKeys.Create();
        Assert.EndsWith(target.Extension(), outputPath);

        List<(ulong, ContentMetaType, uint, string)> expectedTitles;
        using (var original = SwitchPackage.Open(lineage.CartridgeOrigin ? CartridgeNsp() : EShopNsp(), keys))
            expectedTitles = TitleSummary(original);

        using (var pkg = SwitchPackage.Open(outputPath, keys))
        {
            Assert.Equal(ToPackageFormat(target), pkg.Format);
            if (target.IsGameCard())
                Assert.Equal(0, new FileInfo(outputPath).Length % 0x200);

            // Same titles, versions and CNMTs (content IDs) as the original.
            Assert.Equal(expectedTitles, TitleSummary(pkg));
            var plan = Assert.Single(MergePlanner.Plan([pkg]));
            Assert.NotNull(plan.Base);
            Assert.NotNull(plan.Update);
            Assert.Single(plan.Dlc);
            Assert.Equal("Test Game", plan.GetName());

            // Compressed formats hold NCZs (program and data NCAs); the others hold none.
            Assert.Equal(target.IsCompressed(), pkg.Files.Any(f => f.Name.EndsWith(".ncz", StringComparison.OrdinalIgnoreCase)));
            foreach (var title in pkg.Titles)
                Assert.All(title.Contents, c => Assert.True(c.IsPresent));

            // Only eShop packages carry tickets: game cards have none, and once an update has passed through a game
            // card its NCAs hold their key in the key area and need no ticket.
            var expectTickets = !after.PassedThroughCard;
            Assert.Equal(expectTickets, pkg.Tickets.Count > 0);

            foreach (var title in pkg.Titles)
                foreach (var entry in title.Contents.Select(c => c.File!).Append(title.MetaFile))
                {
                    var header = pkg.OpenNca(entry).Header;
                    if (!expectTickets)
                        Assert.False(header.HasRightsId, $"{entry.Name} in {target} still needs a ticket");

                    // NSP/NSZ are always Download (game-card headers are rewritten). A game card keeps what it was given.
                    var expected = after.GameCardHeaders && title.Type != ContentMetaType.Patch
                        ? NcaDistributionType.GameCard
                        : NcaDistributionType.Download;
                    Assert.Equal(expected, header.DistributionType);
                }
        }

        // Every NCA still decrypts to the original data. Compressed output is decompressed first (with verification).
        var uncompressed = target switch
        {
            OutputFormat.Nsz => await Convert(outputPath, OutputFormat.Nsp),
            OutputFormat.Xcz => await Convert(outputPath, OutputFormat.Xci),
            _ => outputPath,
        };
        using (var pkg = SwitchPackage.Open(uncompressed, keys))
            Assert.Equal(ExpectedPlaintext(), PlaintextHashes(pkg));
    }

    // ------------------------------------------------------------------- the matrix

    [Theory]
    [MemberData(nameof(EveryConversion))]
    public async Task Converts(MatrixSource source, OutputFormat target)
    {
        var input = await BuildSource(source);
        var output = await Convert(input, target);
        await AssertConverted(output, source, target);
    }

    [Theory]
    [InlineData(MatrixSource.Nsp, OutputFormat.Nsz)]
    [InlineData(MatrixSource.Nsp, OutputFormat.Xcz)]
    [InlineData(MatrixSource.Nsz, OutputFormat.Xcz)]
    [InlineData(MatrixSource.Xci, OutputFormat.Nsz)]
    [InlineData(MatrixSource.Xcz, OutputFormat.Nsz)]
    [InlineData(MatrixSource.CartridgeXci, OutputFormat.Nsz)]
    [InlineData(MatrixSource.CartridgeXci, OutputFormat.Xcz)]
    public async Task Converts_with_block_mode_ncz(MatrixSource source, OutputFormat target)
    {
        var input = await BuildSource(source);
        var output = await Convert(input, target, NczMode.Block);
        await AssertConverted(output, source, target);

        // And a block-mode package converts onwards to every other format.
        foreach (var next in AllFormats)
            await AssertConverted(await Convert(output, next), Lineage.Of(source).Then(target), next);
    }

    // ------------------------------------------------------------------- round trips

    /// <summary>Converting there and back gives the original file byte for byte when nothing has to change on the way.</summary>
    [Theory]
    [InlineData(MatrixSource.Nsp, OutputFormat.Nsp)]
    [InlineData(MatrixSource.Nsp, OutputFormat.Nsz)]
    [InlineData(MatrixSource.Nsz, OutputFormat.Nsz)]
    [InlineData(MatrixSource.Nsz, OutputFormat.Nsp)]
    [InlineData(MatrixSource.Xci, OutputFormat.Xci)]
    [InlineData(MatrixSource.Xci, OutputFormat.Xcz)]
    [InlineData(MatrixSource.Xcz, OutputFormat.Xcz)]
    [InlineData(MatrixSource.Xcz, OutputFormat.Xci)]
    [InlineData(MatrixSource.CartridgeXci, OutputFormat.Xcz)]
    [InlineData(MatrixSource.CartridgeXcz, OutputFormat.Xci)]
    public async Task Round_trip_is_byte_identical(MatrixSource source, OutputFormat via)
    {
        var input = await BuildSource(source);
        var back = await Convert(await Convert(input, via), Path.GetExtension(input) switch
        {
            ".nsp" => OutputFormat.Nsp,
            ".nsz" => OutputFormat.Nsz,
            ".xci" => OutputFormat.Xci,
            ".xcz" => OutputFormat.Xcz,
            var e => throw new InvalidOperationException(e),
        });
        Assert.Equal(Sha(input), Sha(back));
    }

    /// <summary>
    /// Going from an eShop package through a game card and back loses the tickets (the update's NCAs now carry their
    /// own key), but every title and every NCA's data survives, and NSP → XCI → NSP → XCI is then stable.
    /// </summary>
    [Theory]
    [InlineData(OutputFormat.Nsp, OutputFormat.Xci)]
    [InlineData(OutputFormat.Nsp, OutputFormat.Xcz)]
    [InlineData(OutputFormat.Nsz, OutputFormat.Xci)]
    [InlineData(OutputFormat.Nsz, OutputFormat.Xcz)]
    public async Task Through_a_game_card_and_back_keeps_every_title(OutputFormat eShop, OutputFormat card)
    {
        var start = await BuildSource(eShop == OutputFormat.Nsp ? MatrixSource.Nsp : MatrixSource.Nsz);
        var onCard = await Convert(start, card);
        var back = await Convert(onCard, eShop);
        await AssertConverted(back, MatrixSource.Xci, eShop);

        // Once the tickets are gone, the next trip through the card and back changes nothing.
        var again = await Convert(await Convert(back, card), eShop);
        Assert.Equal(Sha(back), Sha(again));
    }

    /// <summary>One package converted through every format in turn, ending where it started.</summary>
    [Theory]
    [InlineData(OutputFormat.Nsp, OutputFormat.Nsz, OutputFormat.Xci, OutputFormat.Xcz, OutputFormat.Nsp)]
    [InlineData(OutputFormat.Nsp, OutputFormat.Xcz, OutputFormat.Nsz, OutputFormat.Xci, OutputFormat.Nsp)]
    [InlineData(OutputFormat.Nsp, OutputFormat.Xci, OutputFormat.Nsz, OutputFormat.Xcz, OutputFormat.Nsp)]
    public async Task Chain_through_every_format(params OutputFormat[] chain)
    {
        Assert.Equal(OutputFormat.Nsp, chain[0]);
        var path = EShopNsp();
        var lineage = Lineage.Of(MatrixSource.Nsp);
        foreach (var format in chain.Skip(1))
        {
            path = await Convert(path, format);
            await AssertConverted(path, lineage, format);
            lineage = lineage.Then(format);
        }
    }
}
