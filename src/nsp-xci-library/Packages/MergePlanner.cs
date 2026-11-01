using NspXciLibrary.Meta;

namespace NspXciLibrary.Packages;

/// <summary>What goes into one output file: a base game, its newest update and newest DLC.</summary>
public sealed record MergePlan(
    ulong ApplicationId,
    PackageTitle? Base,
    PackageTitle? Update,
    IReadOnlyList<PackageTitle> Dlc,
    IReadOnlyList<string> Warnings)
{
    public IEnumerable<PackageTitle> Titles
    {
        get
        {
            if (Base is not null) yield return Base;
            if (Update is not null) yield return Update;
            foreach (var d in Dlc) yield return d;
        }
    }

    /// <summary>The version a user would see: the update's if present, else the base game's.</summary>
    public uint DisplayVersion => Update?.Version ?? Base?.Version ?? 0;

    /// <summary>The game's name from the update's or base game's control data, if readable.</summary>
    public string? GetName(params int[] preferredLanguages)
        => Update?.GetControlData()?.GetName(preferredLanguages) ?? Base?.GetControlData()?.GetName(preferredLanguages);

    /// <summary>The version string shown on the console (e.g. "1.2.0"), if readable.</summary>
    public string? GetDisplayVersionString()
    {
        var value = (Update ?? Base)?.GetControlData()?.DisplayVersion;
        return string.IsNullOrEmpty(value) ? null : value;
    }
}

public static class MergePlanner
{
    /// <summary>
    /// Groups every title across the given packages by base application and picks, for each group,
    /// the base game, the newest update and the newest version of each DLC. Delta titles are ignored,
    /// and titles with missing content files are left out with a warning.
    /// </summary>
    public static IReadOnlyList<MergePlan> Plan(IEnumerable<SwitchPackage> packages)
        => Plan(packages, out _);

    /// <param name="skipped">Why games with no usable titles at all were left out entirely.</param>
    public static IReadOnlyList<MergePlan> Plan(IEnumerable<SwitchPackage> packages, out IReadOnlyList<string> skipped)
    {
        var skippedList = new List<string>();
        skipped = skippedList;
        var titles = packages.SelectMany(p => p.Titles)
            .Where(t => t.Type is ContentMetaType.Application or ContentMetaType.Patch or ContentMetaType.AddOnContent);

        var plans = new List<MergePlan>();
        foreach (var group in titles.GroupBy(t => t.ApplicationId).OrderBy(g => g.Key))
        {
            var warnings = new List<string>();
            var dropped = new List<string>();
            var usable = new List<PackageTitle>();
            foreach (var t in group)
            {
                var missing = MissingContentCount(t);
                if (missing > 0)
                {
                    var reason = $"{t} in '{Path.GetFileName(t.Package.Path)}' is missing {missing} content file(s); skipped.";
                    warnings.Add(reason);
                    dropped.Add(reason);
                }
                else
                    usable.Add(t);
            }

            var bases = usable.Where(t => t.Type == ContentMetaType.Application).ToList();
            var baseTitle = PickNewest(bases, warnings);
            if (baseTitle is null)
                warnings.Add("No base game found; output will contain only the update and/or DLC.");
            else if (bases.Select(b => b.Version).Distinct().Count() > 1)
                warnings.Add($"Several base game versions found; using v{baseTitle.Version}.");

            var update = PickNewest(usable.Where(t => t.Type == ContentMetaType.Patch), warnings);

            var dlc = usable.Where(t => t.Type == ContentMetaType.AddOnContent)
                .GroupBy(t => t.TitleId)
                .Select(g => PickNewest(g, warnings)!)
                .OrderBy(t => t.TitleId)
                .ToList();

            if (baseTitle is null && update is null && dlc.Count == 0)
            {
                skippedList.AddRange(dropped.Select(w => $"{group.Key:X16}: {w}"));
                continue;
            }

            plans.Add(new MergePlan(group.Key, baseTitle, update, dlc, warnings));
        }
        return plans;
    }

    /// <summary>
    /// The highest version among <paramref name="candidates"/> (the first input wins a tie). Warns when another
    /// input holds a different title under the same ID and version, since only one of them can be kept.
    /// </summary>
    private static PackageTitle? PickNewest(IEnumerable<PackageTitle> candidates, List<string> warnings)
    {
        var ordered = candidates.OrderByDescending(t => t.Version).ToList();
        if (ordered.Count == 0)
            return null;

        var chosen = ordered[0];
        foreach (var other in ordered.Skip(1).TakeWhile(t => t.Version == chosen.Version))
        {
            if (!SameContents(chosen, other))
                warnings.Add($"{chosen} appears in '{Path.GetFileName(chosen.Package.Path)}' and '{Path.GetFileName(other.Package.Path)}' "
                             + $"with different contents; using '{Path.GetFileName(chosen.Package.Path)}'. One of the dumps may be mislabelled.");
        }
        return chosen;
    }

    /// <summary>True when two titles list the same content IDs (the same dump, possibly in different containers).</summary>
    internal static bool SameContents(PackageTitle a, PackageTitle b)
        => a.Meta.Contents.Select(c => c.ContentIdHex).Order().SequenceEqual(b.Meta.Contents.Select(c => c.ContentIdHex).Order());

    /// <summary>Content files (other than optional delta fragments) a title's CNMT lists but its package lacks.</summary>
    public static int MissingContentCount(PackageTitle title)
        => title.Contents.Count(c => !c.IsPresent && c.Record.Type != CnmtContentType.DeltaFragment);
}
