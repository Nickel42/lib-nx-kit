using NoIntrovert.Switch.Keys;
using NoIntrovert.Switch.Meta;
using NoIntrovert.Switch.Packages;

namespace NoIntrovert.Switch;

/// <summary>The files written by a batch operation, plus anything that was left out and why.</summary>
public sealed record ConversionReport(IReadOnlyList<WriteResult> Outputs, IReadOnlyList<string> Skipped);

/// <summary>
/// High-level entry point. Opens any mix of NSP/NSZ/XCI/XCZ files (including split ones), groups
/// their titles by game, and writes merged, converted or split output.
/// </summary>
public sealed class SwitchConverter
{
    private readonly KeySet _keys;

    public SwitchConverter(KeySet keys) => _keys = keys;

    /// <summary>
    /// Inspects files without writing anything — use this to show the user what a merge would contain.
    /// The caller owns the returned packages and must dispose them.
    /// </summary>
    public IReadOnlyList<MergePlan> Preview(IEnumerable<string> inputPaths, out IReadOnlyList<SwitchPackage> openedPackages)
    {
        var packages = OpenAll(inputPaths);
        openedPackages = packages;
        return MergePlanner.Plan(packages);
    }

    /// <summary>
    /// Merges and/or converts the input files, writing one file per game (base + newest update + DLC)
    /// into <paramref name="outputDirectory"/>.
    /// </summary>
    public async Task<ConversionReport> MergeAsync(
        IEnumerable<string> inputPaths,
        string outputDirectory,
        WriteOptions options,
        IProgress<WriteProgress>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(outputDirectory);
        var packages = OpenAll(inputPaths);
        try
        {
            var plans = MergePlanner.Plan(packages, out var skipped);
            var writer = new PackageWriter(_keys);
            var results = new List<WriteResult>();
            foreach (var plan in plans)
            {
                ct.ThrowIfCancellationRequested();
                var path = Path.Combine(outputDirectory, DefaultFileName(plan, options.Format));
                results.Add(await writer.WriteAsync(plan, path, options, progress, ct).ConfigureAwait(false));
            }
            return new ConversionReport(results, skipped);
        }
        finally
        {
            foreach (var p in packages)
                p.Dispose();
        }
    }

    /// <summary>Converts a single file (e.g. NSZ → NSP, XCI → NSZ, NSP → XCI).</summary>
    public Task<ConversionReport> ConvertAsync(
        string inputPath,
        string outputDirectory,
        WriteOptions options,
        IProgress<WriteProgress>? progress = null,
        CancellationToken ct = default)
        => MergeAsync([inputPath], outputDirectory, options, progress, ct);

    /// <summary>
    /// Splits multi-title packages (merged "super" NSPs, multi-game carts) into one file per title:
    /// base game, each update and each DLC separately. Every version found is kept; titles with
    /// missing content files are skipped and reported.
    /// </summary>
    public async Task<ConversionReport> SplitAsync(
        IEnumerable<string> inputPaths,
        string outputDirectory,
        WriteOptions options,
        IProgress<WriteProgress>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(outputDirectory);
        var packages = OpenAll(inputPaths);
        try
        {
            var names = new Dictionary<ulong, string>();
            foreach (var plan in MergePlanner.Plan(packages))
                if (plan.GetName() is { } name)
                    names[plan.ApplicationId] = name;

            var writer = new PackageWriter(_keys);
            var results = new List<WriteResult>();
            var skipped = new List<string>();
            var titles = packages.SelectMany(p => p.Titles)
                .Where(t => t.Type is ContentMetaType.Application or ContentMetaType.Patch or ContentMetaType.AddOnContent)
                .OrderBy(t => t.ApplicationId).ThenBy(t => t.Type).ThenBy(t => t.TitleId).ThenBy(t => t.Version);

            var written = new Dictionary<(ulong, uint), PackageTitle>();
            foreach (var title in titles)
            {
                ct.ThrowIfCancellationRequested();

                if (written.TryGetValue((title.TitleId, title.Version), out var first))
                {
                    // Same title and version already written from another input; only a concern if it differs.
                    if (!MergePlanner.SameContents(first, title))
                        skipped.Add($"{title} in '{Path.GetFileName(title.Package.Path)}' has the same ID and version as the copy in "
                                    + $"'{Path.GetFileName(first.Package.Path)}' but different contents; only the first was written.");
                    continue;
                }

                var missing = MergePlanner.MissingContentCount(title);
                if (missing > 0)
                {
                    // Only report it if no complete copy of this title and version exists elsewhere.
                    if (!titles.Any(t => t.TitleId == title.TitleId && t.Version == title.Version
                                         && MergePlanner.MissingContentCount(t) == 0))
                        skipped.Add($"{title} in '{Path.GetFileName(title.Package.Path)}' is missing {missing} content file(s).");
                    continue;
                }
                written.Add((title.TitleId, title.Version), title);

                var path = Path.Combine(outputDirectory,
                    TitleFileName(title, names.GetValueOrDefault(title.ApplicationId), options.Format));
                results.Add(await writer.WriteAsync([title], path, options, progress, ct).ConfigureAwait(false));
            }
            return new ConversionReport(results, skipped);
        }
        finally
        {
            foreach (var p in packages)
                p.Dispose();
        }
    }

    /// <summary>
    /// "Game Name [0100ABCD12340000][v131072][+3 DLC].nsz", or without the name when it can't be read.
    /// Without a base game: "[UPD]", "[UPD][+2 DLC]" or "[DLC]" / "[3 DLC]".
    /// </summary>
    public static string DefaultFileName(MergePlan plan, OutputFormat format)
    {
        var name = plan.GetName() is { } gameName && Sanitize(gameName) is { Length: > 0 } clean ? clean + " " : "";
        name += $"[{plan.ApplicationId:X16}][v{plan.DisplayVersion}]";

        if (plan.Base is not null || plan.Update is not null)
        {
            if (plan.Base is null)
                name += "[UPD]";
            if (plan.Dlc.Count > 0)
                name += $"[+{plan.Dlc.Count} DLC]";
        }
        else
        {
            name += plan.Dlc.Count == 1 ? "[DLC]" : $"[{plan.Dlc.Count} DLC]";
        }
        return name + format.Extension();
    }

    /// <summary>"Game Name [0100ABCD12341001][v65536][DLC].nsp".</summary>
    public static string TitleFileName(PackageTitle title, string? gameName, OutputFormat format)
    {
        var tag = title.Type switch
        {
            ContentMetaType.Application => "BASE",
            ContentMetaType.Patch => "UPD",
            ContentMetaType.AddOnContent => "DLC",
            _ => title.Type.ToString(),
        };
        var clean = gameName is null ? "" : Sanitize(gameName);
        var prefix = clean.Length > 0 ? clean + " " : "";
        return $"{prefix}[{title.TitleId:X16}][v{title.Version}][{tag}]{format.Extension()}";
    }

    /// <summary>Makes a game name safe as a file name on Windows, macOS and Linux.</summary>
    public static string Sanitize(string name)
    {
        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars()) { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };
        var chars = name.Select(c => invalid.Contains(c) || char.IsControl(c) ? ' ' : c).ToArray();
        var cleaned = string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));

        const int maxLength = 120;
        if (cleaned.Length > maxLength)
        {
            var cut = maxLength;
            if (char.IsHighSurrogate(cleaned[cut - 1]))
                cut--; // don't split a surrogate pair
            cleaned = cleaned[..cut];
        }

        // Windows rejects names ending in a dot or space.
        return cleaned.TrimEnd('.', ' ');
    }

    private List<SwitchPackage> OpenAll(IEnumerable<string> paths)
    {
        var packages = new List<SwitchPackage>();
        try
        {
            foreach (var path in paths)
                packages.Add(SwitchPackage.Open(path, _keys));
            return packages;
        }
        catch
        {
            foreach (var p in packages)
                p.Dispose();
            throw;
        }
    }
}
