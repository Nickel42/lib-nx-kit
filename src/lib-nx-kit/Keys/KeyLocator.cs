namespace LibNxKit.Keys;

/// <summary>
/// Finds the user's own <c>prod.keys</c> (and the <c>title.keys</c> beside it) where Switch tools and
/// emulators keep them, so an app needs no key setting of its own unless the user wants one.
/// </summary>
public static class KeyLocator
{
    public const string ProdKeysFileName = "prod.keys";
    public const string TitleKeysFileName = "title.keys";

    /// <summary>The emulators whose key folders are searched, in this order.</summary>
    private static readonly string[] YuzuFamily = ["yuzu", "eden", "citron", "sudachi", "suyu"];

    /// <summary>
    /// The folders searched for <c>prod.keys</c>, in order: Ryujinx's <c>system</c> folder, then the yuzu family's
    /// <c>keys</c> folders (yuzu, Eden, Citron, Sudachi, Suyu), each in its per-platform data folder, then
    /// <c>~/.switch</c> (hactool, nsz) and <c>keys/</c> beside the executable.
    /// </summary>
    public static IReadOnlyList<string> DefaultDirectories()
    {
        var list = new List<string>();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData); // Windows: %APPDATA%; Unix: ~/.config
        var xdgData = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } d ? d : Path.Combine(home, ".local", "share");
        var xdgConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } c ? c : Path.Combine(home, ".config");
        var macSupport = Path.Combine(home, "Library", "Application Support");

        if (OperatingSystem.IsWindows())
        {
            list.Add(Path.Combine(roaming, "Ryujinx", "system"));
            list.AddRange(YuzuFamily.Select(n => Path.Combine(roaming, n, "keys")));
        }
        else if (OperatingSystem.IsMacOS())
        {
            list.Add(Path.Combine(macSupport, "Ryujinx", "system"));
            list.AddRange(YuzuFamily.Select(n => Path.Combine(macSupport, n, "keys")));
        }
        else
        {
            list.Add(Path.Combine(xdgConfig, "Ryujinx", "system"));
            list.AddRange(YuzuFamily.Select(n => Path.Combine(xdgData, n, "keys")));
        }
        list.Add(Path.Combine(home, ".switch"));
        list.Add(Path.Combine(AppContext.BaseDirectory, "keys"));
        return list;
    }

    /// <summary>
    /// Where <c>prod.keys</c> is: each of <paramref name="extraLocations"/> first (a <c>prod.keys</c> file, or a folder
    /// holding one), then <see cref="DefaultDirectories"/>. Null when there's none.
    /// </summary>
    public static string? FindProdKeys(IEnumerable<string>? extraLocations = null)
    {
        foreach (var location in (extraLocations ?? []).Where(l => !string.IsNullOrWhiteSpace(l)))
        {
            if (File.Exists(location)) return Path.GetFullPath(location);
            var inFolder = Path.Combine(location, ProdKeysFileName);
            if (File.Exists(inFolder)) return Path.GetFullPath(inFolder);
        }
        return DefaultDirectories().Select(d => Path.Combine(d, ProdKeysFileName)).FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// Loads the first <c>prod.keys</c> <see cref="FindProdKeys"/> finds, with the <c>title.keys</c> beside it when there
    /// is one. Null (with <paramref name="prodKeysPath"/> null) when none was found.
    /// </summary>
    public static KeySet? LoadDefault(out string? prodKeysPath, IEnumerable<string>? extraLocations = null)
    {
        prodKeysPath = FindProdKeys(extraLocations);
        if (prodKeysPath is null) return null;
        var titleKeys = Path.Combine(Path.GetDirectoryName(prodKeysPath)!, TitleKeysFileName);
        return KeySet.Load(prodKeysPath, File.Exists(titleKeys) ? titleKeys : null);
    }
}
