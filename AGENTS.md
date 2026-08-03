# NfsSharp Agent Guide

## Repository Overview

NfsSharp is a managed .NET NFS client SDK. The public packages are:

- `src/NfsSharp.Client`: the high-level NFSv3 client facade and experimental NFSv4 COMPOUND client.
- `src/NfsSharp.Protocol`: XDR, RPC, NFSv3/NFSv4 protocol types, status mapping, and RPCSEC_GSS abstractions.
- `tests/NfsSharp.Tests`: xUnit unit tests and opt-in NFSv3 integration tests.

The solution is `NfsSharp.sln`. Library projects target `net8.0`, `net9.0`, and `net10.0`; the test project targets `net9.0`.

## Working Agreements

- Keep changes focused. Do not modify generated output, IDE metadata, package artifacts, or unrelated files.
- Preserve public API compatibility unless the task explicitly calls for a breaking change.
- Use nullable reference types, async APIs, `CancellationToken` support, and `IAsyncDisposable` patterns consistently with nearby code.
- Avoid adding dependencies unless they are necessary and compatible with every supported target framework.
- Do not add credentials, tokens, server addresses, or other environment-specific values to tracked files.
- Do not include the assistant's name or identifying marker in repository content, commit messages, branch names, or pull-request text.
- Update documentation and `CHANGELOG.md` when a public behavior, compatibility claim, or supported scope changes.

## Protocol and Client Changes

- Ground NFS/RPC wire changes in the relevant specification and keep encoded field ordering, alignment, and status handling explicit.
- Add focused tests for XDR encoding/decoding, response parsing, status mapping, and failure paths when protocol behavior changes.
- Treat NFSv3 as the primary supported API. NFSv4 APIs are experimental; do not imply parity or interoperability coverage without evidence.
- Maintain the existing separation between protocol models in `NfsSharp.Protocol` and client orchestration in `NfsSharp.Client`.

## Build and Test

Run these commands from the repository root:

```powershell
dotnet restore NfsSharp.sln
dotnet build NfsSharp.sln --configuration Release --no-restore
dotnet test NfsSharp.sln --configuration Release --no-build --no-restore
```

For NFSv3 wire behavior, mount/export handling, file operations, retries, caching, or compatibility changes, also run the Docker-backed integration suite described in `tests/integration/README.md`:

```powershell
docker compose -f compose.integration.yml up --build --detach --wait --wait-timeout 90
$env:NFSSHARP_RUN_NFSV3_INTEGRATION = "1"
$env:NFSSHARP_NFS_SERVER = "127.0.0.1"
$env:NFSSHARP_NFS_EXPORT = "/export"
$env:NFSSHARP_NFS_UID = "0"
$env:NFSSHARP_NFS_GID = "0"
dotnet test tests/NfsSharp.Tests/NfsSharp.Tests.csproj --configuration Release --filter "Category=Integration"
docker compose -f compose.integration.yml down --volumes --remove-orphans
```

Always tear down the integration fixture, including after a failing test run.

## Packaging and Pull Requests

- Validate packages with `dotnet pack NfsSharp.sln --configuration Release --no-build --output artifacts/packages` when package metadata or distributable content changes.
- Keep `master` releasable. Use focused short-lived branches and target pull requests at `master`.
- When a pull request completes an Issue, include `Closes #<issue-number>` in its description so merging automatically closes the Issue.
- Describe validation performed and any tests intentionally not run. Do not commit files ignored by `.gitignore`.

## Releases

- Prepare a focused release PR before tagging: update `src/Directory.Build.props` to the intended package version, move completed entries from `CHANGELOG.md` `Unreleased` into a dated release section, and keep package documentation and compatibility claims aligned.
- Before merging the release PR, run `dotnet restore NfsSharp.sln`, `dotnet build NfsSharp.sln --configuration Release --no-restore`, `dotnet test NfsSharp.sln --configuration Release --no-build --no-restore`, and `dotnet pack NfsSharp.sln --configuration Release --no-build --output artifacts/packages`.
- Run the Docker-backed NFSv3 integration suite for releases that affect NFSv3 wire behavior, mount/export handling, file operations, retries, caching, or compatibility claims; always tear down the fixture afterward.
- Publish only a tested commit reachable from `master` by pushing a plain SemVer tag (for example `1.1.2`). The `Release NuGet` workflow builds, tests, packs, uploads `.nupkg` and `.snupkg` artifacts, and publishes to NuGet.org through Trusted Publishing.
- Verify the release workflow succeeds and that its GitHub Actions artifacts contain the expected package and symbol package before announcing the release. Never store NuGet API keys or other release credentials in the repository.
