# lib-nx-kit

A C# library for reading, converting, merging, splitting and compressing Nintendo Switch content packages in any .NET app. Every format converts to every other: NSP, NSZ, XCI and XCZ in and out, including split-file inputs. Base games, updates and DLC merge into one file per game. Every NCA is checked against its metadata hash while writing. The only dependency is ZstdSharp.Port (pure managed zstd, MIT). No native code, no keys bundled.

[![CI](https://github.com/Nickel42/lib-nx-kit/actions/workflows/ci.yml/badge.svg)](https://github.com/Nickel42/lib-nx-kit/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](https://github.com/Nickel42/lib-nx-kit/blob/main/LICENSE)

Package: **lib-nx-kit** (namespace `LibNxKit`).

## Platforms

The library and its one dependency are plain managed code, so the same package works everywhere .NET runs. It targets `net8.0` and `net10.0` and is marked trim- and AOT-compatible.

| Platform | Status |
|---|---|
| Windows (x64, Arm64) | Tested in CI on every change |
| Linux (x64, Arm64) | Tested in CI on every change |
| macOS (Apple silicon, Intel) | Tested in CI on every change |
| Android | An Android app using the library (NSZ compression included) is built, trimmed, in CI on every change |
| iOS, Mac Catalyst, Native AOT | Expected to work; not tested in CI |

Any app on .NET 8 or later can use it, including `net8.0-android`, `net9.0-ios` and so on. .NET Framework and .NET Standard are not supported.

## Install

```bash
dotnet add package lib-nx-kit
```

Until a release reaches nuget.org, reference `src/lib-nx-kit` directly or use the package attached to a [GitHub release](https://github.com/Nickel42/lib-nx-kit/releases).

## What it does

Inputs can be NSP, NSZ, XCI or XCZ files, or split files: `*.ns0`/`*.xc0` parts, `*.00` parts, or a folder of parts named `00`, `01`, ….

- **Converting:** any input format to any output format, with `ConvertAsync`.
- **Merging (`MergeAsync`):** any mix of input files is grouped by base game. Each group becomes one file with the base game, the newest update and the newest version of each DLC.
- **Splitting (`SplitAsync`):** multi-title packages ("super" NSPs, multi-game carts) become one file per base game, update and DLC.
- **Names:** the game name, publisher and display version are read from the control NCA (`MergePlan.GetName()`, `GetDisplayVersionString()`, `PackageTitle.GetControlData()`). Output files are named like `Game Name [0100…][v131072][+3 DLC].nsz`.
- **NSZ/XCZ:** solid mode (the nsz default; smallest output) and block mode (independent blocks, fastest). Both use `MaxDegreeOfParallelism` threads: solid mode through zstd's worker threads, which still write one standard zstd frame. Only Program and PublicData NCAs are compressed, like nsz. Control, manual and meta NCAs, and any NCA whose key is unavailable, are stored as-is (the latter with a warning).
- **XCI/XCZ output:** title-key (rights-ID) NCAs are converted to standard crypto, with the title key moved into the key area. Game cards carry no tickets. The card header's RSA signature and encrypted card-info block are left zeroed, because they can't be produced without Nintendo's keys. Emulators and installers accept this; a real console's game-card reader would not.
- **XCI → NSP:** game-card NCA headers are switched to the "download" distribution type so the console will install them (`ConvertGameCardToDownload`). XCI output keeps each NCA's distribution type as it was.
- **Update deltas:** dropped by default (`IncludeDeltaFragments`).
- **Verification:** each NCA's SHA-256 is checked against its CNMT record while writing (`Verify`, on by default). A failed check deletes the partial output.
- **Merge conflicts:** when two inputs hold the same title ID and version with different contents (e.g. a mislabelled DLC dump), the first input is used and the plan carries a warning naming both files. `SplitAsync` reports the second copy under `Skipped`.
- **Writing:** output streams straight to a `.part` file with no temp copies, and is renamed when complete. Progress reporting and cancellation are supported throughout.

**Verification of converted content:** an NCA converted from title-key crypto (for an XCI) legitimately no longer matches its CNMT hash. When verification fails, the writer re-reads that one file and rebuilds candidate original headers: rights ID restored, key area cleared, and either distribution type. If one matches, the file passes with a note, so the library's own NSP → XCI → NSP → NSZ chains verify fully. This only costs time on the rare mismatch path. If a tool changed a header in a way that can't be rebuilt, a Download-type NCA inside a game card gets a warning. Anything else is reported as corrupt.

**Not yet:** writing split output for FAT32 cards, NCA2/NCA0 content, personalized tickets, and reading names from a Control NCA that another tool compressed.

## Keys

The library ships no keys. Users supply their own `prod.keys` (and optionally `title.keys`), dumped from their own console with Lockpick_RCM. `KeySet` derives `titlekek_XX` and `key_area_key_*_XX` from `master_key_XX` when the file has the sources but not the derived keys. A missing key throws `MissingKeyException`, whose `KeyName` says which one.

`KeyLocator` finds them where Switch tools keep them, so an app needs no key setting unless the user wants one. It looks in each location you pass first (a `prod.keys` file, or a folder holding one), then in Ryujinx's `system` folder and the `keys` folders of yuzu, Eden, Citron, Sudachi and Suyu (on Windows, macOS and Linux), `~/.switch`, and `keys/` beside the executable. `LoadDefault` loads the first `prod.keys` it finds, with the `title.keys` beside it:

```csharp
KeySet? keys = KeyLocator.LoadDefault(out var foundAt, extraLocations: [mySettingsPath]);
```

## Quick start

These snippets come from [`samples/QuickStart`](https://github.com/Nickel42/lib-nx-kit/tree/main/samples/QuickStart), which CI compiles on every change.

```csharp
using LibNxKit;
using LibNxKit.Keys;
using LibNxKit.Ncz;
using LibNxKit.Packages;

var keys = KeySet.Load(prodKeysPath, titleKeysPath);   // titleKeysPath may be null
var converter = new SwitchConverter(keys);

// Base + update + DLC (any formats) → one NSZ per game
ConversionReport report = await converter.MergeAsync(
    new[] { "game.xci", "update.nsp", "dlc1.nsz" },
    outputDirectory,
    new WriteOptions { Format = OutputFormat.Nsz, Ncz = new NczOptions { Mode = NczMode.Block } },
    new Progress<WriteProgress>(p => Console.Write($"\r{p.Fraction:P0} {p.CurrentFile}")),
    cancellationToken);
// report.Outputs: each file written (path, size, warnings). report.Skipped: anything left out and why.

// Single conversion (any format to any format, split inputs included)
await converter.ConvertAsync("game.nsz", outputDirectory, new WriteOptions { Format = OutputFormat.Nsp });
await converter.ConvertAsync("game.xc0", outputDirectory, new WriteOptions { Format = OutputFormat.Xcz });

// One file per title out of a merged package
await converter.SplitAsync(new[] { "everything.nsp" }, outputDirectory, new WriteOptions());
```

For a preview ("this will contain v1.2.0 and 3 DLC"), plan without writing:

```csharp
var plans = converter.Preview(paths, out var packages);
foreach (var plan in plans)
{
    // plan.GetName(), plan.GetDisplayVersionString(), plan.Base, plan.Update, plan.Dlc, plan.Warnings
}
foreach (var p in packages) p.Dispose();
```

## Using it in an app

- **Threads:** run long operations off the UI thread (`Task.Run`). In WPF, WinForms, Avalonia or MAUI, create `Progress<T>` on the UI thread, so reports arrive there.
- **Memory:** everything streams; there are no temporary copies of whole files.
- **Android and iOS:** the APIs take file paths. Files picked through the Storage Access Framework (Android) or document picker (iOS) arrive as streams or content URIs; copy them into your app's storage first. See [`samples/AndroidSmoke`](https://github.com/Nickel42/lib-nx-kit/tree/main/samples/AndroidSmoke), which loads `prod.keys` from the app's files folder.

## Layout

```
src/lib-nx-kit/
  Keys/        KeySet (prod.keys / title.keys, key derivation)
  Crypto/      Nintendo AES-XTS (NCA headers), AES-CTR (NCA bodies), ECB helpers
  IO/          SubStream, decrypting stream, copy helpers
  Containers/  PFS0 (NSP/NSZ), HFS0 + XCI secure partition
  Nca/         NCA3 header, section access, tickets
  Meta/        CNMT (title metadata), NACP (control data)
  Ncz/         NCZ section table, compressor (solid/block), decompressor
  Packages/    SwitchPackage (open any format), MergePlanner, PackageWriter
  SwitchConverter.cs   high-level facade
tests/
  lib-nx-kit.Tests/        xUnit; builds synthetic NCAs with fake keys, no real content needed
  vectors.py               independent reference for the crypto test vectors
samples/
  QuickStart/              the code above, as a program
  AndroidSmoke/            a minimal Android app using the library
```

## Building and testing

Needs the .NET 10 SDK (the tests also run on .NET 8 if it is installed).

```bash
dotnet build
dotnet test
dotnet pack -c Release -o artifacts
```

The tests don't need real keys or games. They build structurally valid NCAs encrypted with fake keys, then exercise everything end to end: header decryption, key-area decryption, section reads, NCZ in both modes, NSP → NSZ → NSP byte for byte, merges, splits and failed verification. The XTS and CTR vectors come from an independent implementation (`tests/vectors.py`).

[docs/TESTING.md](https://github.com/Nickel42/lib-nx-kit/blob/main/docs/TESTING.md) has the real-dump checklist, what has been checked so far, and the open questions. Releases are described in [CONTRIBUTING.md](https://github.com/Nickel42/lib-nx-kit/blob/main/CONTRIBUTING.md).

## Legal

This library is for working with content you dumped from hardware and software you own. It contains no Nintendo keys or other copyrighted Nintendo material; the tests use generated fake keys. "Nintendo Switch" is a trademark of Nintendo; this project is not affiliated with or endorsed by Nintendo.

## License

[MIT](https://github.com/Nickel42/lib-nx-kit/blob/main/LICENSE) © 2026 Nickel42. The one dependency, [ZstdSharp.Port](https://github.com/oleg-st/ZstdSharp), is also MIT-licensed.
