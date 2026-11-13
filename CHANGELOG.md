# Changelog

All notable changes are listed here. Versions follow [Semantic Versioning](https://semver.org/); while the version is 0.x, minor versions may change the API.

## 0.2.0

- **Conversion tests:** every source (NSP, NSZ, XCI, XCZ, and a cartridge dump as XCI or XCZ) converted to every output format, checking titles, tickets, NCA headers and decrypted data, plus block-mode NCZ, byte-exact round trips and chains through all four formats.
- **Exact round trips:** `WriteOptions.KeepSourceLayout` (on by default) writes files in their source package's order, and keeps an NSP's or NSZ's header padding when it becomes an NSP or NSZ with the same files. An NSP compressed to NSZ and back is now the original byte for byte; before, each title was written meta first, so the file order (and the file's hash) could change. Set it to false for the old order.
- **`KeyLocator`** finds `prod.keys` (and `title.keys` beside it) in Ryujinx's and the yuzu family's folders, `~/.switch` and `keys/` beside the executable, after any locations you pass.
- **Shared test fixtures** in `tests/fixtures/`: a synthetic base-game NSP, an update NSP with its ticket, and the fake `prod.keys` that opens them, for apps to test against. A test checks they still equal what the test helpers make.
- **ZstdSharp.Port 0.8.6** (was 0.8.1), the version lib-wii-u-kit and lib-3ds-kit use, so an app using them together gets one zstd. No API changes.
- **Renamed** the repository and the NuGet package from `nsp-xci-library` to `lib-nx-kit` (assembly `lib-nx-kit`; the namespace is now `LibNxKit`).

## 0.1.0

First release as a standalone library.

- **Package** `nsp-xci-library`, namespace `NspXciLibrary`.
- **Targets** `net8.0` and `net10.0`, marked trim- and AOT-compatible; works on Windows, Linux, macOS and Android.
- **NuGet metadata:** MIT license, source link and symbol packages.
- **CI:** tests on Windows, Linux and macOS (.NET 8 and .NET 10), plus a trimmed Android app build.
- **Release workflow:** a `v*` tag publishes the package to a GitHub release (and to nuget.org when configured).
- **Samples:** `QuickStart` (the README's code) and `AndroidSmoke`.
- **Solid NSZ/XCZ compression** uses zstd worker threads (`MaxDegreeOfParallelism`), about 10× faster with an unchanged format.
- **Merging and splitting** report two inputs that hold the same title ID and version with different contents.
