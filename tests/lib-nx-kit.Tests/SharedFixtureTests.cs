using System.Runtime.CompilerServices;
using LibNxKit.Keys;
using LibNxKit.Packages;
using Xunit;

namespace LibNxKit.Tests;

/// <summary>
/// <c>tests/fixtures/</c>: a small synthetic NSP and the fake <c>prod.keys</c> that opens it, for apps that use this
/// library to test against (NoIntrovert does). They're built by the same helpers as every other test, so this test
/// checks the committed files still equal what the helpers make. Set <c>LIB_NX_KIT_WRITE_FIXTURES=1</c> to rewrite them.
/// </summary>
public sealed class SharedFixtureTests
{
    private static string FixturesDir([CallerFilePath] string source = "") =>
        Path.Combine(Path.GetDirectoryName(source)!, "..", "fixtures");

    private static string FakeProdKeys() =>
        "# Made-up keys for the synthetic test packages in this folder. They open nothing real.\n"
        + $"header_key = {Convert.ToHexString(TestKeys.HeaderKey).ToLowerInvariant()}\n"
        + $"key_area_key_application_00 = {Convert.ToHexString(TestKeys.KeyAreaKey).ToLowerInvariant()}\n"
        + $"titlekek_00 = {Convert.ToHexString(TestKeys.TitleKek).ToLowerInvariant()}\n";

    [Fact]
    public void CommittedFixturesAreWhatTheHelpersMake()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lib-nx-kit-fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "prod.keys.fake"), FakeProdKeys());
            TestTitles.WriteNsp(Path.Combine(dir, "test_game.nsp"), TestTitles.Base());
            TestTitles.WriteNsp(Path.Combine(dir, "test_game_update.nsp"), TestTitles.Update());

            var committed = FixturesDir();
            if (Environment.GetEnvironmentVariable("LIB_NX_KIT_WRITE_FIXTURES") == "1")
            {
                Directory.CreateDirectory(committed);
                foreach (var f in Directory.GetFiles(dir)) File.Copy(f, Path.Combine(committed, Path.GetFileName(f)), overwrite: true);
            }
            foreach (var f in Directory.GetFiles(dir))
            {
                var name = Path.GetFileName(f);
                var path = Path.Combine(committed, name);
                Assert.True(File.Exists(path), $"tests/fixtures/{name} is missing; run with LIB_NX_KIT_WRITE_FIXTURES=1");
                Assert.True(File.ReadAllBytes(f).AsSpan().SequenceEqual(File.ReadAllBytes(path)),
                    $"tests/fixtures/{name} differs from what the helpers make; run with LIB_NX_KIT_WRITE_FIXTURES=1");
            }
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void TheFakeKeysOpenTheFixturePackage()
    {
        var keys = KeySet.Load(Path.Combine(FixturesDir(), "prod.keys.fake"));
        using var package = SwitchPackage.Open(Path.Combine(FixturesDir(), "test_game.nsp"), keys);
        Assert.Equal(TestTitles.BaseId, Assert.Single(package.Titles).TitleId);
    }
}

public sealed class KeyLocatorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lib-nx-kit-keys-" + Guid.NewGuid().ToString("N"));

    public KeyLocatorTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string Keys => $"header_key = {Convert.ToHexString(TestKeys.HeaderKey)}\n";

    [Fact]
    public void AFileOrAFolderGivenFirstWins()
    {
        var folder = Path.Combine(_dir, "mine");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "prod.keys"), Keys);
        var file = Path.Combine(_dir, "other.keys");
        File.WriteAllText(file, Keys);

        Assert.Equal(Path.GetFullPath(Path.Combine(folder, "prod.keys")), KeyLocator.FindProdKeys([folder]));
        Assert.Equal(Path.GetFullPath(file), KeyLocator.FindProdKeys([file, folder]));
        Assert.Equal(Path.GetFullPath(Path.Combine(folder, "prod.keys")), KeyLocator.FindProdKeys(["", Path.Combine(_dir, "missing"), folder]));
    }

    [Fact]
    public void LoadsTitleKeysBesideProdKeys()
    {
        File.WriteAllText(Path.Combine(_dir, "prod.keys"), Keys);
        var rightsId = TestNca.RightsIdFor(TestTitles.UpdateId);
        File.WriteAllText(Path.Combine(_dir, "title.keys"), $"{Convert.ToHexString(rightsId)} = {Convert.ToHexString(new byte[16])}\n");
        var keys = KeyLocator.LoadDefault(out var path, [_dir]);
        Assert.NotNull(keys);
        Assert.Equal(Path.GetFullPath(Path.Combine(_dir, "prod.keys")), path);
        Assert.True(keys!.HasHeaderKey);
        Assert.True(keys.TryGetEncryptedTitleKey(rightsId, out _));
    }

    [Fact]
    public void SearchesTheEmulatorsFolders()
    {
        var dirs = KeyLocator.DefaultDirectories();
        Assert.Contains(dirs, d => d.Contains("Ryujinx", StringComparison.Ordinal));
        Assert.Contains(dirs, d => d.Contains("yuzu", StringComparison.Ordinal));
        Assert.Contains(dirs, d => d.EndsWith(".switch", StringComparison.Ordinal));
    }
}
