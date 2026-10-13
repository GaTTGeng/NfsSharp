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

Review commits since the previous release tag and prepare a focused release PR that:

- updates `src/Directory.Build.props` to the intended package version;
- moves completed entries from `Unreleased` into a dated `CHANGELOG.md` section and leaves a fresh empty `Unreleased` section;
- keeps README, compatibility, and release documentation aligned with the version and support scope;
- passes `dotnet restore NfsSharp.sln`;
- passes `dotnet build NfsSharp.sln --configuration Release --no-restore`;
- passes `dotnet test NfsSharp.sln --configuration Release --no-build --no-restore`;
- passes `dotnet pack NfsSharp.sln --configuration Release --no-build --output artifacts/packages`.

For changes to NFSv3 wire behavior, mount/export handling, file operations, retries, caching, or compatibility claims, also run the Docker-backed integration suite in [the integration guide](../tests/integration/README.md) and tear down its fixture afterward.

After the PR is merged, push a plain NuGet SemVer tag from the tested commit on `master`:

```powershell
git tag <version>
git push origin <version>
```

The tag triggers `Release NuGet`, which builds, tests, packs, uploads packages, and publishes through NuGet Trusted Publishing. Merging the PR does not publish the package or create a GitHub Release page. Wait for the tag workflow to finish successfully and confirm its `nuget-packages-<version>` artifact contains `.nupkg` and `.snupkg` files for both packages. NuGet indexing can take several minutes; workflow success is the publishing signal.

## Create the GitHub Release page

The GitHub Release page is a separate manual step. Download the artifact from the successful tag workflow and attach all four package files:

```powershell
$version = "<version>"
$runId = "<release-workflow-run-id>"
$artifactDirectory = "artifacts/release-assets"
$releaseNotesPath = "artifacts/release-notes.md" # Prepare this file using the checklist below.
$releaseCommit = gh run view $runId --repo GaTTGeng/NfsSharp --json headSha --jq .headSha
if ($LASTEXITCODE -ne 0 -or -not $releaseCommit) { throw "Could not read the release workflow commit." }
$tagRef = "refs/tags/$version"
$remoteTagRefs = @(git ls-remote --tags origin $tagRef "$tagRef^{}")
if ($LASTEXITCODE -ne 0) { throw "Could not check the remote tag." }
$peeledTag = $remoteTagRefs | Where-Object { $_ -match "\s$([regex]::Escape($tagRef))\^\{\}$" }
$directTag = $remoteTagRefs | Where-Object { $_ -match "\s$([regex]::Escape($tagRef))$" }
if ($peeledTag) {
  $tagCommit = ($peeledTag -split "\s+")[0]
} elseif ($directTag) {
  $tagCommit = ($directTag -split "\s+")[0]
} else {
  # workflow_dispatch has no triggering tag, so create it at the tested workflow commit.
  git fetch origin master
  if ($LASTEXITCODE -ne 0) { throw "Could not fetch origin/master." }
  git cat-file -e "$releaseCommit^{commit}" 2>$null
  if ($LASTEXITCODE -ne 0) { throw "Release workflow commit $releaseCommit is not available locally after fetching master." }
  git merge-base --is-ancestor $releaseCommit origin/master
  if ($LASTEXITCODE -ne 0) { throw "Release workflow commit $releaseCommit is not reachable from origin/master." }
  git tag $version $releaseCommit
  if ($LASTEXITCODE -ne 0) { throw "Could not create tag $version at $releaseCommit." }
  git push origin $version
  if ($LASTEXITCODE -ne 0) { throw "Could not push tag $version." }
  $tagCommit = $releaseCommit
}
if ($tagCommit -ne $releaseCommit) { throw "Tag $version points to $tagCommit, not the release workflow commit $releaseCommit." }
gh run download $runId --repo GaTTGeng/NfsSharp --name "nuget-packages-$version" --dir $artifactDirectory
$assets = @(Get-ChildItem -LiteralPath $artifactDirectory -File | Where-Object { $_.Extension -in ".nupkg", ".snupkg" } | ForEach-Object FullName)
if ($assets.Count -ne 4) { throw "Expected four package and symbol assets; found $($assets.Count)." }
$releaseOptions = @()
if ($version -match '^[0-9]+\.[0-9]+\.[0-9]+-') { $releaseOptions += "--prerelease" }
gh release create $version --repo GaTTGeng/NfsSharp --title "NfsSharp $version" --target $releaseCommit --verify-tag --notes-file $releaseNotesPath @releaseOptions @assets
```

For a normal tag-triggered release, the existing remote tag must point to the exact commit used by that run. For a manual `workflow_dispatch` release, this command creates and pushes the version tag at that exact commit if the tag is absent. It stops if an existing tag points elsewhere. Versions with a prerelease suffix such as `1.4.0-preview.1` are marked as prereleases on GitHub.

Write release notes from the dated changelog section. Include an install command, a NuGet link for each package at the released version, concise user-facing highlights, the compatibility boundary and known scope (including that NFSv4 and RPCSEC_GSS remain experimental), and a link to the versioned `CHANGELOG.md`.

Verify the page and every package asset:

```powershell
gh release view <version> --repo GaTTGeng/NfsSharp --json name,body,assets,url
```

Do not announce the release until the workflow succeeded and the GitHub Release page contains the expected notes and package assets. If publishing fails, fix and rerun that workflow; never reuse a version that has already been published.

To run a manual release, open the `Release NuGet` workflow, enter the package version, and enable publishing.

## Re-run a failed release

After fixing workflow configuration, re-run the failed workflow from GitHub Actions or with:

```powershell
gh run rerun <run-id> --repo GaTTGeng/NfsSharp
```
