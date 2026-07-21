# Maintainer Release Guide

This repository publishes NuGet packages with NuGet Trusted Publishing from GitHub Actions. Do not create or store a long-lived NuGet API key in GitHub secrets for normal releases.

## One-time NuGet.org setup

Create a trusted publishing policy on NuGet.org for each NfsSharp package:

- Package Owner: the NuGet.org account that owns the NfsSharp packages.
- Repository Owner: `GaTTGeng`
- Repository: `NfsSharp`
- Workflow File: `release-nuget.yml`
- Environment: leave empty unless the workflow is later changed to use a GitHub environment.

The workflow uses `NuGet/login@v1`, so `.github/workflows/release-nuget.yml` must keep the `id-token: write` permission.

## GitHub setup

The repository variable `NUGET_USER` should contain the NuGet.org username used for Trusted Publishing. If it is not set, the workflow falls back to the GitHub repository owner.

No `NUGET_API_KEY` repository secret is required.

## Release a version

Before tagging, prepare and review a release PR that:

- updates `src/Directory.Build.props` to the intended package version;
- moves completed entries from `Unreleased` into a dated `CHANGELOG.md` section;
- keeps README, compatibility, and release documentation aligned with the version and support scope;
- passes `dotnet test NfsSharp.sln --configuration Release`;
- passes `dotnet pack NfsSharp.sln --configuration Release --no-build --output artifacts/packages`.

Use NuGet SemVer tags without a leading `v`:

```powershell
git tag <version>
git push origin <version>
```

The release workflow strips a leading `v` if one is used, but plain SemVer tags match the NuGet package version directly.

The release commit must be reachable from `master`.

To run a manual release, open the `Release NuGet` workflow, enter the package version, and enable publishing.

## Re-run a failed release

After fixing workflow configuration, re-run the failed workflow from GitHub Actions or with:

```powershell
gh run rerun <run-id> --repo GaTTGeng/NfsSharp
```
