using System.Security.Cryptography;
using NoIntrovert.Switch.Meta;
using NoIntrovert.Switch.Ncz;
using NoIntrovert.Switch.Packages;

namespace NoIntrovert.Switch.Tests;

/// <summary>Split inputs, game names, multi-title merges, XCI/XCZ output and splitting packages.</summary>
public sealed class FeatureTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("nointrovert-switch-features").FullName;
    private readonly SwitchConverter _converter = new(TestKeys.Create());

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string Nsp(string name, params TestTitle[] titles)
    {
        var path = Path.Combine(_dir, name);
        TestTitles.WriteNsp(path, titles);
        return path;
    }

    private static WriteOptions Fast(OutputFormat format) => new() { Format = format, Ncz = new NczOptions { Level = 3 } };

    private static byte[] ReadEntry(SwitchPackage pkg, string name)
    {
        var entry = pkg.FindFile(name) ?? throw new Xunit.Sdk.XunitException($"{name} not found in {pkg.Path}");
        using var s = pkg.OpenFile(entry);
        var bytes = new byte[s.Length];
        s.ReadExactly(bytes);
        return bytes;
    }

    // ------------------------------------------------------------- split input

    [Fact]
    public void Split_ns0_parts_open_as_one_package()
    {
        var baseGame = TestTitles.Base();
        var whole = File.ReadAllBytes(Nsp("whole.nsp", baseGame));

        // Cut at awkward offsets so files and headers straddle part boundaries.
        var cuts = new[] { 0, 0x1234, whole.Length / 2 + 7, whole.Length };
        for (var i = 0; i < 3; i++)
            File.WriteAllBytes(Path.Combine(_dir, $"split.ns{i}"), whole[cuts[i]..cuts[i + 1]]);

        using var pkg = SwitchPackage.Open(Path.Combine(_dir, "split.ns0"), TestKeys.Create());
        Assert.Equal(3, pkg.Parts.Count);
        Assert.Single(pkg.Titles);
        Assert.Equal(baseGame.MainNca, ReadEntry(pkg, TestNca.ContentIdHex(baseGame.MainNca) + ".nca"));
    }

    [Fact]
    public void Folder_of_numbered_parts_opens_as_one_package()
    {
        var baseGame = TestTitles.Base();
        var whole = File.ReadAllBytes(Nsp("whole.nsp", baseGame));
        var folder = Directory.CreateDirectory(Path.Combine(_dir, "Game.nsp")).FullName;
        File.WriteAllBytes(Path.Combine(folder, "00"), whole[..0x30001]);
        File.WriteAllBytes(Path.Combine(folder, "01"), whole[0x30001..]);

        using var pkg = SwitchPackage.Open(folder, TestKeys.Create());
        Assert.Equal(2, pkg.Parts.Count);
        Assert.Equal(baseGame.MainNca, ReadEntry(pkg, TestNca.ContentIdHex(baseGame.MainNca) + ".nca"));
    }

    // ------------------------------------------------------------- names

    [Fact]
    public void Game_name_and_display_version_come_from_control_data()
    {
        using var baseOnly = SwitchPackage.Open(Nsp("base.nsp", TestTitles.Base("Base Name")), TestKeys.Create());
        var plan = Assert.Single(MergePlanner.Plan([baseOnly]));
        Assert.Equal("Base Name", plan.GetName());
        Assert.Equal("1.0.0", plan.GetDisplayVersionString());
        Assert.Equal("Test Publisher", plan.Base!.GetControlData()!.GetPublisher());

        // The update's control data wins: newer name and version. Its control NCA uses title-key crypto.
        using var withUpdate = SwitchPackage.Open(
            Nsp("both.nsp", TestTitles.Base("Base Name"), TestTitles.Update(name: "Renamed Game", displayVersion: "1.1.0")),
            TestKeys.Create());
        plan = Assert.Single(MergePlanner.Plan([withUpdate]));
        Assert.Equal("Renamed Game", plan.GetName());
        Assert.Equal("1.1.0", plan.GetDisplayVersionString());
        Assert.StartsWith("Renamed Game [0100EEEE00000000][v65536]", SwitchConverter.DefaultFileName(plan, OutputFormat.Nsp));
    }

    [Theory]
    [InlineData("Zelda: Tears of the Kingdom", "Zelda Tears of the Kingdom")]
    [InlineData("What?  A/B  Game...", "What A B Game")]
    [InlineData("Game .", "Game")]
    public void Sanitize_produces_portable_file_names(string input, string expected)
        => Assert.Equal(expected, SwitchConverter.Sanitize(input));

    // ------------------------------------------------------------- merging

    [Fact]
    public async Task Base_update_and_dlc_from_separate_files_merge_into_one_nsz_and_back()
    {
        var baseGame = TestTitles.Base();
        var update = TestTitles.Update();
        var dlc = TestTitles.Dlc();
        var inputs = new[] { Nsp("b.nsp", baseGame), Nsp("u.nsp", update), Nsp("d.nsp", dlc) };

        var outDir = Path.Combine(_dir, "merged");
        var result = Assert.Single((await _converter.MergeAsync(inputs, outDir, Fast(OutputFormat.Nsz))).Outputs);
        Assert.Equal("Test Game [0100EEEE00000000][v65536][+1 DLC].nsz", Path.GetFileName(result.OutputPath));

        using (var merged = SwitchPackage.Open(result.OutputPath, TestKeys.Create()))
        {
            Assert.Equal(PackageFormat.Nsz, merged.Format);
            Assert.Equal(3, merged.Titles.Count);
            Assert.Single(merged.Tickets); // the update's ticket travels with it
            Assert.Contains(merged.Files, f => f.Name.EndsWith(".ncz"));
        }

        var back = Assert.Single((await _converter.ConvertAsync(result.OutputPath, Path.Combine(_dir, "back"), Fast(OutputFormat.Nsp))).Outputs);
        using var nsp = SwitchPackage.Open(back.OutputPath, TestKeys.Create());
        Assert.Equal(PackageFormat.Nsp, nsp.Format);
        foreach (var title in new[] { baseGame, update, dlc })
            Assert.Equal(SHA256.HashData(title.MainNca),
                SHA256.HashData(ReadEntry(nsp, TestNca.ContentIdHex(title.MainNca) + ".nca")));
    }

    [Fact]
    public void Planner_keeps_only_the_newest_update()
    {
        using var a = SwitchPackage.Open(Nsp("a.nsp", TestTitles.Base(), TestTitles.Update(0x10000)), TestKeys.Create());
        using var b = SwitchPackage.Open(Nsp("b.nsp", TestTitles.Update(0x20000)), TestKeys.Create());
        var plan = Assert.Single(MergePlanner.Plan([a, b]));
        Assert.Equal(0x20000u, plan.Update!.Version);
        Assert.Equal(0x20000u, plan.DisplayVersion);
    }

    [Fact]
    public void Planner_warns_when_two_inputs_hold_different_dlc_with_the_same_id_and_version()
    {
        using var a = SwitchPackage.Open(Nsp("a.nsp", TestTitles.Base(), TestTitles.Dlc(seed: 3)), TestKeys.Create());
        using var b = SwitchPackage.Open(Nsp("b.nsp", TestTitles.Dlc(seed: 4)), TestKeys.Create());
        var plan = Assert.Single(MergePlanner.Plan([a, b]));

        var dlc = Assert.Single(plan.Dlc);
        Assert.Same(a, dlc.Package); // first input wins the tie
        var warning = Assert.Single(plan.Warnings, w => w.Contains("different contents"));
        Assert.Contains("'a.nsp'", warning);
        Assert.Contains("'b.nsp'", warning);
    }

    [Fact]
    public void Planner_stays_quiet_when_the_same_dlc_is_given_twice()
    {
        using var a = SwitchPackage.Open(Nsp("a.nsp", TestTitles.Base(), TestTitles.Dlc()), TestKeys.Create());
        using var b = SwitchPackage.Open(Nsp("b.nsp", TestTitles.Dlc()), TestKeys.Create());
        var plan = Assert.Single(MergePlanner.Plan([a, b]));
        Assert.Single(plan.Dlc);
        Assert.DoesNotContain(plan.Warnings, w => w.Contains("different contents"));
    }

    [Fact]
    public async Task Merge_reports_the_conflict_in_the_output_warnings()
    {
        var a = Nsp("a.nsp", TestTitles.Base(), TestTitles.Dlc(seed: 3));
        var b = Nsp("b.nsp", TestTitles.Dlc(seed: 4));
        var output = Assert.Single((await _converter.MergeAsync([a, b], Path.Combine(_dir, "merged"), Fast(OutputFormat.Nsp))).Outputs);
        Assert.Contains(output.Warnings, w => w.Contains("different contents"));
    }

    // ------------------------------------------------------------- game card output

    [Theory]
    [InlineData(OutputFormat.Xci, PackageFormat.Xci)]
    [InlineData(OutputFormat.Xcz, PackageFormat.Xcz)]
    public async Task Merged_game_card_image_strips_rights_and_reads_back(OutputFormat output, PackageFormat expected)
    {
        var baseGame = TestTitles.Base();
        var update = TestTitles.Update();
        var dlc = TestTitles.Dlc();
        var input = Nsp("all.nsp", baseGame, update, dlc);

        var card = Assert.Single((await _converter.MergeAsync([input], Path.Combine(_dir, "card"), Fast(output))).Outputs);
        Assert.EndsWith(output == OutputFormat.Xci ? ".xci" : ".xcz", card.OutputPath);
        Assert.Equal(0, new FileInfo(card.OutputPath).Length % 0x200);

        using (var pkg = SwitchPackage.Open(card.OutputPath, TestKeys.Create()))
        {
            Assert.Equal(expected, pkg.Format);
            Assert.Equal(3, pkg.Titles.Count);
            Assert.Empty(pkg.Tickets); // game cards carry no tickets...

            // ...so the update's title-key NCA now holds its key in the key area and still decrypts.
            var updateTitle = pkg.Titles.Single(t => t.Type == ContentMetaType.Patch);
            var program = updateTitle.Contents.Single(c => c.Record.Type == CnmtContentType.Program).File!;
            var reader = pkg.OpenNca(program);
            Assert.False(reader.Header.HasRightsId);
            if (expected == PackageFormat.Xci)
            {
                using var section = reader.OpenSection(reader.Header.Sections[0]);
                var plain = new byte[0x50000];
                section.ReadExactly(plain);
                Assert.Equal(Det.Compressible(0x50000, 2), plain);
            }
            Assert.Equal("Test Game", Assert.Single(MergePlanner.Plan([pkg])).GetName());
        }

        // Back to NSP with verification on: untouched NCAs verify directly; the update's converted NCAs
        // verify against their reconstructed original (title-key) headers.
        var nsp = Assert.Single((await _converter.ConvertAsync(card.OutputPath, Path.Combine(_dir, "nsp"),
            new WriteOptions { Format = OutputFormat.Nsp, Verify = true })).Outputs);
        Assert.Contains(nsp.Warnings, w => w.Contains("verified against its original form"));
        Assert.DoesNotContain(nsp.Warnings, w => w.Contains("can't be verified"));
        using (var back = SwitchPackage.Open(nsp.OutputPath, TestKeys.Create()))
        {
            Assert.Equal(baseGame.MainNca, ReadEntry(back, TestNca.ContentIdHex(baseGame.MainNca) + ".nca"));
            Assert.Equal(dlc.MainNca, ReadEntry(back, TestNca.ContentIdHex(dlc.MainNca) + ".nca"));
        }

        // That NSP is no longer a game card, and converting it again with verification on still works.
        var again = Assert.Single((await _converter.ConvertAsync(nsp.OutputPath, Path.Combine(_dir, "again"),
            new WriteOptions { Format = OutputFormat.Nsz, Verify = true, Ncz = new NczOptions { Level = 3 } })).Outputs);
        Assert.True(File.Exists(again.OutputPath));
    }

    [Fact]
    public void File_names_without_a_base_game_say_what_they_hold()
    {
        using var updateAndDlc = SwitchPackage.Open(Nsp("ud.nsp", TestTitles.Update(), TestTitles.Dlc()), TestKeys.Create());
        var plan = Assert.Single(MergePlanner.Plan([updateAndDlc]));
        Assert.Equal("Test Game [0100EEEE00000000][v65536][UPD][+1 DLC].nsp", SwitchConverter.DefaultFileName(plan, OutputFormat.Nsp));

        using var dlcOnly = SwitchPackage.Open(Nsp("d.nsp", TestTitles.Dlc()), TestKeys.Create());
        plan = Assert.Single(MergePlanner.Plan([dlcOnly]));
        Assert.Equal("[0100EEEE00000000][v0][DLC].xcz", SwitchConverter.DefaultFileName(plan, OutputFormat.Xcz));
    }

    // ------------------------------------------------------------- splitting

    [Fact]
    public async Task Split_skips_and_reports_titles_with_missing_content()
    {
        var baseGame = TestTitles.Base();
        var dlc = TestTitles.Dlc();
        // Drop the DLC's data NCA but keep its meta NCA, as a broken dump would.
        var files = baseGame.Files.Concat(dlc.Files.Where(f => f.Name.EndsWith(".cnmt.nca"))).ToList();
        var input = Path.Combine(_dir, "broken.nsp");
        TestNca.WriteNsp(input, files);

        var report = await _converter.SplitAsync([input], Path.Combine(_dir, "split-broken"), Fast(OutputFormat.Nsp));
        Assert.Single(report.Outputs);
        Assert.Contains(report.Skipped, s => s.Contains("missing 1 content file"));
    }

    [Fact]
    public async Task Split_reports_a_second_title_with_the_same_id_and_version_but_different_contents()
    {
        var a = Nsp("a.nsp", TestTitles.Dlc(seed: 3));
        var b = Nsp("b.nsp", TestTitles.Dlc(seed: 4));
        var report = await _converter.SplitAsync([a, b], Path.Combine(_dir, "split-conflict"), Fast(OutputFormat.Nsp));
        Assert.Single(report.Outputs);
        Assert.Contains(report.Skipped, s => s.Contains("different contents") && s.Contains("'b.nsp'"));
    }

    [Fact]
    public async Task Multi_title_package_splits_into_one_file_per_title()
    {
        var input = Nsp("super.nsp", TestTitles.Base(), TestTitles.Update(), TestTitles.Dlc());
        var report = await _converter.SplitAsync([input], Path.Combine(_dir, "split"), Fast(OutputFormat.Nsp));
        Assert.Empty(report.Skipped);
        var results = report.Outputs;

        var names = results.Select(r => Path.GetFileName(r.OutputPath)).ToList();
        Assert.Equal(3, names.Count);
        Assert.Contains("Test Game [0100EEEE00000000][v0][BASE].nsp", names);
        Assert.Contains("Test Game [0100EEEE00000800][v65536][UPD].nsp", names);
        Assert.Contains("Test Game [0100EEEE00001001][v0][DLC].nsp", names);

        foreach (var r in results)
        {
            using var pkg = SwitchPackage.Open(r.OutputPath, TestKeys.Create());
            Assert.Single(pkg.Titles);
            var isUpdate = pkg.Titles[0].Type == ContentMetaType.Patch;
            Assert.Equal(isUpdate ? 1 : 0, pkg.Tickets.Count);
        }
    }
}
