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
4. If the repository has a `NUGET_USER` variable, the workflow also pushes the package to nuget.org with [Trusted Publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing): GitHub issues a short-lived OIDC token and nuget.org exchanges it for an API key that lasts an hour. No API key is stored in the repository.

### Setting up nuget.org publishing (once)

1. On nuget.org, open your user menu → **Trusted Publishing** and add a policy:
   - **Repository Owner:** `Nickel42`
   - **Repository:** `lib-nx-kit`
   - **Workflow File:** `release.yml`
   - **Environment:** `nuget`
2. In the GitHub repository, set the variable `NUGET_USER` (Settings → Secrets and variables → Actions → Variables) to the nuget.org profile name that owns the policy — the profile name, not the email address.
3. Optionally, under Settings → Environments → `nuget` (created by the first release run, or create it yourself), limit deployments to tags matching `v*` and add yourself as a required reviewer.

Only the workflow's `nuget` job can request the OIDC token, and nuget.org accepts it only from that workflow file and environment. If an older `NUGET_API_KEY` secret exists, delete it and revoke the key on nuget.org.
