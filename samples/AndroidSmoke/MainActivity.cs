using Android.App;
using Android.OS;
using Android.Widget;
using NspXciLibrary;
using NspXciLibrary.Keys;
using NspXciLibrary.Ncz;
using NspXciLibrary.Packages;

namespace AndroidSmoke;

/// <summary>
/// Calls into the library on Android, including NSZ compression through ZstdSharp, so the trimmed
/// Release build in CI analyzes those paths. On a device, put prod.keys and an .nsp/.xci in the
/// app's files folder to see it converted to .nsz.
/// </summary>
[Activity(Label = "Switch library smoke test", MainLauncher = true)]
public class MainActivity : Activity
{
    protected override async void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        var text = new TextView(this);
        SetContentView(text);

        string filesDir = FilesDir?.AbsolutePath ?? "";
        string prodKeys = Path.Combine(filesDir, "prod.keys");
        text.Text = $"Files folder: {filesDir}\nSanitized name: {SwitchConverter.Sanitize("Game: Name?")}\n";
        if (!File.Exists(prodKeys))
        {
            text.Text += "No prod.keys found.";
            return;
        }

        var inputs = Directory.EnumerateFiles(filesDir)
            .Where(f => f.EndsWith(".nsp", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".xci", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var converter = new SwitchConverter(KeySet.Load(prodKeys));
        var report = await Task.Run(() => converter.MergeAsync(inputs, Path.Combine(filesDir, "out"),
            new WriteOptions { Format = OutputFormat.Nsz, Ncz = new NczOptions { Mode = NczMode.Block } }));
        text.Text += string.Join("\n", report.Outputs.Select(o => $"Wrote {Path.GetFileName(o.OutputPath)} ({o.Size:N0} bytes)"));
    }
}
