# Contributing

Issues and pull requests are welcome.

## Building and testing

```bash
dotnet build
dotnet test
```

You need the .NET 10 SDK. Install the .NET 8 runtime too, to run the net8.0 tests. To build the Android sample:

```bash
dotnet workload install android
dotnet build samples/AndroidSmoke -c Release
```

## Guidelines

- **No keys or copyrighted files.** Never commit real keys, tickets, game files or dumps, not even in tests. Tests use generated fake keys and synthetic NCAs.
- **Formats:** changes should come with a test that builds the case synthetically.
- **Warnings:** keep the build free of warnings, including the trim and AOT analyzers.

## Releasing

1. Update `CHANGELOG.md` and `VersionPrefix` in `Directory.Build.props`.
2. Tag the commit and push the tag:
   ```bash
   git tag v0.1.0
   git push origin v0.1.0
   ```
3. The **Release** workflow tests and packs the library with that version and creates a GitHub release with the packages attached.
4. If the repository has a `NUGET_API_KEY` secret (an API key from nuget.org scoped to `lib-nx-kit`), the workflow also pushes the package to nuget.org.
