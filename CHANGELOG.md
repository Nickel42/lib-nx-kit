# Changelog

All notable changes are listed here. Versions follow [Semantic Versioning](https://semver.org/); while the version is 0.x, minor versions may change the API.

## Unreleased

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
