# NFSv3 Integration Tests

The integration suite uses a repository-owned NFS-Ganesha container with the in-memory FSAL. It exposes only NFSv3 over TCP and does not mount an NFS file system on the host.

## CI evidence

The `CI` workflow publishes NFSv3 evidence separately from the normal build, unit-test, and pack job:

| CI job | Purpose | Artifacts |
| --- | --- | --- |
| `Build, test, and pack` | Restores, builds, runs non-Docker test coverage, packs NuGet artifacts, and uploads `.trx` test results. | `test-results`, `nuget-packages` |
| `NFSv3 integration` | Starts the repository NFS-Ganesha server and runs tests marked `Category=Integration`. | `nfs-v3-integration-results` |

The integration artifact includes test results plus `docker compose ps --all` output and NFS server logs captured even when tests fail. A pull request that changes verified NFSv3 behavior should have both CI jobs passing, or the PR should explain why integration coverage is not relevant.

## Run locally

Start the server:

```powershell
docker compose -f compose.integration.yml up --build --detach --wait
```

Run the integration tests:

```powershell
$env:NFSSHARP_RUN_NFSV3_INTEGRATION = "1"
$env:NFSSHARP_NFS_SERVER = "127.0.0.1"
$env:NFSSHARP_NFS_EXPORT = "/export"
$env:NFSSHARP_NFS_EXPECTED_EXPORT_GROUP = "*"
dotnet test NfsSharp.sln --configuration Release --filter "Category=Integration"
```

If host port `111/tcp` is already in use, map the container portmapper to an alternate host port and pass the same port to the tests:

```powershell
$env:NFSSHARP_INTEGRATION_PORTMAP_PORT = "11111"
docker compose -f compose.integration.yml up --build --detach --wait

$env:NFSSHARP_RUN_NFSV3_INTEGRATION = "1"
$env:NFSSHARP_NFS_PORTMAP_PORT = "11111"
dotnet test NfsSharp.sln --configuration Release --filter "Category=Integration"
```

Inspect logs and stop the server:

```powershell
docker compose -f compose.integration.yml logs --no-color
docker compose -f compose.integration.yml down --volumes --remove-orphans
```

Without `NFSSHARP_RUN_NFSV3_INTEGRATION=1`, the integration tests are skipped and the normal unit-test workflow does not require Docker.

## Fixture layout

When the integration tests connect, they create an idempotent fixture tree under `nfssharp-fixtures` in the export. Shared fixture paths are treated as read-only test data:

| Path | Purpose |
| --- | --- |
| `nfssharp-fixtures/empty-dir` | Empty directory lookup and enumeration case. |
| `nfssharp-fixtures/nested/child/data.bin` | Nested binary file with deterministic byte content. |
| `nfssharp-fixtures/empty.txt` | Empty regular file. |
| `nfssharp-fixtures/hello.txt` | Small text file and hard-link source. |
| `nfssharp-fixtures/unicode-\u6d4b\u8bd5.txt` | Unicode path component case. |
| `nfssharp-fixtures/boundary-...` | 255-character name boundary case. |
| `nfssharp-fixtures/hello-link` | Symbolic link to `hello.txt`, when supported by the server. |
| `nfssharp-fixtures/hello-hardlink` | Hard link to `hello.txt`, when supported by the server. |
| `nfssharp-fixtures/no-access` | Permission-denied candidate with mode `000`, when mode bits are enforced by the server and AUTH_SYS identity. |
| `nfssharp-fixtures/runs/run-*` | Per-test mutable workspace, removed by the fixture cleanup path. |

The repository-owned Ganesha MEM server supports the common file, directory, symlink, hard-link, and mode-bit fixture cases. External servers may expose different behavior for symlink, hard-link, timestamp, or permission enforcement; tests discover those optional capabilities and only assert them when the server accepts the setup.

## Test environment

| Variable | Default | Purpose |
| --- | --- | --- |
| `NFSSHARP_RUN_NFSV3_INTEGRATION` | unset | Set to `1` to enable real-server tests. |
| `NFSSHARP_NFS_SERVER` | `127.0.0.1` | NFS server address. |
| `NFSSHARP_NFS_EXPORT` | `/export` | NFSv3 export path. |
| `NFSSHARP_NFS_PORTMAP_PORT` | `111` | Host TCP port used to reach portmapper/rpcbind. Set this when `NFSSHARP_INTEGRATION_PORTMAP_PORT` maps the repository container to a non-default host port. |
| `NFSSHARP_NFS_EXPECTED_EXPORT_GROUP` | `*` only when server and export use implicit defaults; otherwise unset | Optional access group to assert in mountd export-list results. Leave unset for external servers where the advertised group is server-specific or empty. |
| `NFSSHARP_NFS_UID` | `0` | AUTH_SYS user ID. |
| `NFSSHARP_NFS_GID` | `0` | AUTH_SYS primary group ID. |

The image uses Ubuntu 24.04 pinned by OCI digest and installs the Ubuntu package versions of NFS-Ganesha, its in-memory FSAL, and rpcbind. The exported fixture is reached through portmapper v2, mount protocol v3, and NFS protocol v3 over TCP. Tests use AUTH_SYS credentials from `NFSSHARP_NFS_UID` and `NFSSHARP_NFS_GID`; the CI defaults are UID `0` and GID `0`. The container health check verifies portmapper v2, mount protocol v3, and NFS protocol v3 before tests run.

## M1 completion evidence

M1 is ready to evaluate when milestone issues and CI show evidence for:

| Area | Evidence source |
| --- | --- |
| Export, mount, metadata, and lifecycle | M1 issues, `NfsV3Client_ListsAdvertisedExportAndAccessGroups`, mount/unmount integration tests, and `NFSv3 integration` CI. |
| Directory enumeration | READDIR/READDIRPLUS integration tests for empty, small, nested, and paginated directories. |
| File I/O and persistence | Read, write, stability mode, verifier, and COMMIT integration tests. |
| Mutations and links | Create/remove, rename, symlink, hard-link, and attribute mutation integration tests. |
| Failure semantics | Status-preserving integration tests for reproducible NFSv3 errors and documented gaps for statuses the repository server cannot produce reliably. |
| Documentation sync | `docs/nfs-compatibility.md`, this integration guide, README links, and PR checklist entries updated with matching test evidence. |
