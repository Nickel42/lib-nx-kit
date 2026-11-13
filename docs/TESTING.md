# Testing

## 1. Unit tests

```bash
dotnet build
dotnet test
```

The tests run against both library targets (net8.0 and net10.0); CI runs them on Windows, Linux and macOS and builds the Android sample. They build synthetic NCAs encrypted with fake keys, so they need no real keys or games. The crypto vectors come from an independent Python implementation, `tests/vectors.py` (needs `pip install cryptography`).

`ConversionMatrixTests` converts a base game + update (with ticket) + DLC from every source (NSP, NSZ, XCI, XCZ, and a cartridge dump with GameCard NCAs as XCI or XCZ) to every output format, then checks the output's format, titles and CNMTs, tickets, NCA headers (rights IDs, distribution type) and that every NCA still decrypts to the original data. It also covers block-mode NCZ, byte-identical round trips, round trips through a game card, and chains through all four formats.

## 2. Real-dump checklist

1. **Round trip:** NSP → NSZ → NSP, and compare the files inside with the original.
2. **Interop with `nsz`:** `nsz -D` should decompress our NSZ files, and we should read theirs.
3. **Installing:** install merged output with your installer of choice.
4. **Emulator:** boot a merged XCI/XCZ in an emulator and confirm the update and DLC apply.
5. **Split dumps:** open a split dump (`.xc0` or a `00`/`01` folder) and convert it.

### What has been checked on real dumps

- **Opening:** 35 files open and decrypt: 18 base games (NSP and XCI, including trimmed XCIs and cartridges with an update on them), 7 updates and 10 DLC. Names, publishers and versions read correctly.
- **Round trips:**
  - NSP → NSZ (solid and block) → NSP keeps every file inside identical.
  - XCI → XCZ → XCI is byte-identical.
  - XCI → NSP → XCI changes only the 16-byte block at 0x200 that holds the distribution type.
- **Merging and splitting:**
  - Base + update + DLC merges to NSP, XCI and NSZ, picking the newest update (also over one stored on a cartridge).
  - Splitting a merged NSP gives back the base and DLC exactly. Updates lose only their delta fragments, which are dropped by default.
- **Conflicts:** two different DLC dumps with the same ID and version produce a warning naming both files.
- **Cancellation** during NSP, NSZ, XCI and XCZ writes leaves no files behind.
- **Speed:** solid NSZ of a 1.2 GB title takes about 34 s with worker threads (versus 393 s single-threaded), and the output decompresses identically.

Not yet checked: interop with the `nsz` tool, installing on a console, booting in an emulator, and split dumps.

## 3. Open questions

- **Block-mode NCZ `decompressedSize`:** tools may disagree on whether this field counts the raw 0x4000-byte header. The reader accepts both; the writer excludes the header. Confirm with `nsz` before making block mode a default.
- **Gamecard → download patch:** this changes the NCA header bytes, so the patched NCA's hash no longer matches its content ID, and the header's RSA signature no longer validates (installing needs sigpatches, exactly as with 4NXCI/NSC_Builder output). Verification accepts both the original and the patched header, so converted files can be re-converted with `Verify` on.
- **Real nsz files:** test reading an nsz-made NCZ whose first section starts below 0x4000, to confirm nsz's stream layout matches ours there.
