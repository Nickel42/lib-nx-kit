// The examples from the README, as a program CI compiles on every change.
// usage: QuickStart <prod.keys> <output-folder> <nsp|nsz|xci|xcz> <input>... [--title-keys <title.keys>]
using LibNxKit;
using LibNxKit.Keys;
using LibNxKit.Ncz;
using LibNxKit.Packages;

if (args.Length < 4)
{
    Console.WriteLine("usage: QuickStart <prod.keys> <output-folder> <nsp|nsz|xci|xcz> <input>... [--title-keys <title.keys>]");
    return 1;
}
var rest = args.ToList();
string? titleKeys = null;
int tk = rest.IndexOf("--title-keys");
if (tk >= 0 && tk + 1 < rest.Count) { titleKeys = rest[tk + 1]; rest.RemoveRange(tk, 2); }
string prodKeys = rest[0], outputFolder = rest[1];
var format = Enum.Parse<OutputFormat>(rest[2], ignoreCase: true);
var inputs = rest.Skip(3).ToList();

// --- Load keys (the user's own prod.keys / title.keys) -----------------------------------
var keys = KeySet.Load(prodKeys, titleKeys);
var converter = new SwitchConverter(keys);

// --- Preview: what a merge would contain, without writing anything ------------------------
var plans = converter.Preview(inputs, out var packages);
try
{
    foreach (var plan in plans)
    {
        Console.WriteLine($"{plan.GetName() ?? "(unnamed)"} {plan.GetDisplayVersionString()}: "
                          + $"base {(plan.Base is null ? "no" : "yes")}, update {plan.Update?.Version.ToString() ?? "none"}, {plan.Dlc.Count} DLC");
        foreach (var w in plan.Warnings) Console.WriteLine($"  warning: {w}");
    }
}
finally
{
    foreach (var p in packages) p.Dispose();
}

// --- Merge and/or convert: one output file per game ---------------------------------------
var options = new WriteOptions
{
    Format = format,
    Ncz = new NczOptions { Mode = NczMode.Block }, // Solid (the default) is smaller; Block is faster
};
var progress = new Progress<WriteProgress>(p => Console.Write($"\r{p.Fraction:P0} {p.CurrentFile}        "));
try
{
    ConversionReport report = await converter.MergeAsync(inputs, outputFolder, options, progress);
    Console.WriteLine();
    foreach (var output in report.Outputs)
    {
        Console.WriteLine($"Wrote {output.OutputPath} ({output.Size:N0} bytes)");
        foreach (var w in output.Warnings) Console.WriteLine($"  note: {w}");
    }
    foreach (var s in report.Skipped) Console.WriteLine($"Skipped: {s}");
}
catch (MissingKeyException ex)
{
    Console.WriteLine($"\nMissing key: {ex.KeyName}");
    return 2;
}
return 0;
