# NoIntrovert.Switch

A small, dependency-light .NET 8 library for reading, converting, merging and compressing Nintendo Switch content packages, built for the NoIntrovert Avalonia app.

The only dependency is `ZstdSharp.Port` (pure managed zstd, MIT). Format and crypto code uses the BCL's AES.

## What works

Every input format converts to every output format: NSP, NSZ, XCI and XCZ in, NSP, NSZ, XCI and XCZ out. Inputs can also be split files: `*.ns0`/`*.xc0` parts, `*.00` parts, or a folder of parts named `00`, `01`, ….

- **Merging (`MergeAsync`):** any mix of input files is grouped by base game. Each group becomes one file with the base game, the newest update and the newest version of each DLC.
- **Splitting (`SplitAsync`):** multi-title packages ("super" NSPs, multi-game carts) become one file per base game, update and DLC.
- **Names:** the game name, publisher and display version are read from the control NCA (`MergePlan.GetName()`, `GetDisplayVersionString()`, `PackageTitle.GetControlData()`). Output files are named like `Game Name [0100…][v131072][+3 DLC].nsz`.
- **XCI/XCZ output:** title-key (rights-ID) NCAs are converted to standard crypto, with the title key moved into the key area. Game cards carry no tickets. The card header's RSA signature and encrypted card-info block are left zeroed, because they can't be produced without Nintendo's keys. Emulators and installers accept this; a real console's game-card reader would not.
- **Update deltas:** dropped by default (`IncludeDeltaFragments`).
- **Verification:** each NCA's SHA-256 is checked against its CNMT record while writing (`Verify`, on by default). A failed check deletes the partial output.
- **XCI → NSP:** game-card NCA headers are switched to the "download" distribution type so the console will install them (`ConvertGameCardToDownload`).
- **NSZ:** solid mode (the nsz default; smallest output) and block mode (independent blocks, fastest). Both use `MaxDegreeOfParallelism` threads: solid mode through zstd's worker threads, which still write one standard zstd frame.
- **Merge conflicts:** when two inputs hold the same title ID and version with different contents (e.g. a mislabelled DLC dump), the first input is used and the plan carries a warning naming both files. `SplitAsync` reports the second copy under `Skipped`.
- **Writing:** output streams straight to a `.part` file with no temp copies, and is renamed when complete. Progress reporting and cancellation are supported throughout.

**Verification of converted content:** an NCA converted from title-key crypto (for an XCI) legitimately no longer matches its CNMT hash. When verification fails, the writer re-reads that one file and rebuilds candidate original headers: rights ID restored, key area cleared, and either distribution type. If one matches, the file passes with a note, so the library's own NSP → XCI → NSP → NSZ chains verify fully. This only costs time on the rare mismatch path. If a tool changed a header in a way that can't be rebuilt, a Download-type NCA inside a game card gets a warning. Anything else is reported as corrupt.

XCI output keeps each NCA's distribution type as it was (eShop content stays Download). Emulators don't mind. NSC_Builder sets GameCard instead; reading its XCIs back is covered by the rule above.

**Not yet:** writing split output for FAT32 cards, NCA2/NCA0 content, personalized tickets, and reading names from a Control NCA that another tool compressed.

## Keys

Users supply their own `prod.keys` (and optionally `title.keys`) dumped from their console with Lockpick_RCM. **Never ship keys with the app.** `KeySet` derives `titlekek_XX` and `key_area_key_*_XX` from `master_key_XX` when the file has the sources but not the derived keys.

## Usage

```csharp
using NoIntrovert.Switch;
using NoIntrovert.Switch.Keys;
using NoIntrovert.Switch.Packages;

var keys = KeySet.Load(prodKeysPath, titleKeysPath);
var converter = new SwitchConverter(keys);

// Base + update + DLC (any formats) → one NSZ per game
var report = await converter.MergeAsync(
    new[] { "game.xci", "update.nsp", "dlc1.nsz" },
    outputDirectory,
    new WriteOptions { Format = OutputFormat.Nsz },
    new Progress<WriteProgress>(p => ProgressBar.Value = p.Fraction * 100),
    cancellationToken);
// report.Outputs: each file written (path, size, warnings). report.Skipped: anything left out and why.

// Single conversion (any format to any format, split inputs included)
await converter.ConvertAsync("game.nsz", outputDirectory, new WriteOptions { Format = OutputFormat.Nsp });
await converter.ConvertAsync("game.xc0", outputDirectory, new WriteOptions { Format = OutputFormat.Xcz });

// One file per title out of a merged package
await converter.SplitAsync(new[] { "everything.nsp" }, outputDirectory, new WriteOptions());
```

For a preview UI ("this will contain v1.2.0 and 3 DLC"), open the packages and plan without writing:

```csharp
var packages = paths.Select(p => SwitchPackage.Open(p, keys)).ToList();
foreach (var plan in MergePlanner.Plan(packages))
{
    // plan.Base, plan.Update, plan.Dlc, plan.Warnings
    await new PackageWriter(keys).WriteAsync(plan, outputPath, options, progress, ct);
}
packages.ForEach(p => p.Dispose());
```

Run long operations off the UI thread (`Task.Run`). `Progress<T>` marshals reports back to Avalonia's UI thread for you.

## Layout

```
src/NoIntrovert.Switch/
  Keys/        KeySet (prod.keys / title.keys, key derivation)
  Crypto/      Nintendo AES-XTS (NCA headers), AES-CTR (NCA bodies), ECB helpers
  IO/          SubStream, decrypting stream, copy helpers
  Containers/  PFS0 (NSP/NSZ), HFS0 + XCI secure partition
  Nca/         NCA3 header, section access, tickets
  Meta/        CNMT (title metadata)
  Ncz/         NCZ section table, compressor (solid/block), decompressor
  Packages/    SwitchPackage (open any format), MergePlanner, PackageWriter
  SwitchConverter.cs   high-level facade
tests/
  NoIntrovert.Switch.Tests/   xUnit; builds synthetic NCAs with fake keys, no real content needed
  vectors.py                  independent reference for the crypto test vectors
```

## Testing

```
dotnet test
```

The tests don't need real keys or games. They build structurally valid NCAs encrypted with fake keys, then exercise everything end to end: header decryption, key-area decryption, section reads, NCZ in both modes, NSP → NSZ → NSP byte-for-byte, and failed verification. The XTS and CTR vectors come from an independent implementation (`tests/vectors.py`).

Before shipping, also test against real dumps:

1. Round-trip an NSP → NSZ → NSP and compare hashes with the original.
2. Compare NSZ output against the reference `nsz` tool (`nsz -D` should decompress our NSZ files, and we should read theirs).
3. Install merged output with your installer of choice.
4. Boot a merged XCI/XCZ in an emulator (Ryujinx forks, etc.) and confirm the update and DLC apply.
5. Open a split dump (`.xc0` or `00`/`01` folder) and convert it.

## Open questions to check against real files

- **Block-mode NCZ `decompressedSize`:** tools may disagree on whether this field counts the raw 0x4000-byte header. The reader accepts both; the writer excludes the header. Confirm with `nsz` before making block mode a default.
- **Gamecard → download patch:** this changes the NCA header bytes, so the patched NCA's hash no longer matches its content ID, and the header's RSA signature no longer validates (installing needs sigpatches, exactly as with 4NXCI/NSC_Builder output). Verification accepts both the original and the patched header, so converted files can be re-converted with `Verify` on.
- **Real nsz files:** test reading an nsz-made NCZ whose first section starts below 0x4000, to confirm nsz's stream layout matches ours there.

NSZ output compresses only Program and PublicData NCAs, like nsz. Control, manual and meta NCAs, and any NCA whose key is unavailable, are stored as-is (the latter with a warning).
