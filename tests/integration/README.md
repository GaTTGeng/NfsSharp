# NFSv3 Integration Tests

The integration suite uses a repository-owned NFS-Ganesha container with the in-memory FSAL. It exposes only NFSv3 over TCP and does not mount an NFS file system on the host.

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

Inspect logs and stop the server:

```powershell
docker compose -f compose.integration.yml logs --no-color
docker compose -f compose.integration.yml down --volumes --remove-orphans
```

Without `NFSSHARP_RUN_NFSV3_INTEGRATION=1`, the integration tests are skipped and the normal unit-test workflow does not require Docker.

## Test environment

| Variable | Default | Purpose |
| --- | --- | --- |
| `NFSSHARP_RUN_NFSV3_INTEGRATION` | unset | Set to `1` to enable real-server tests. |
| `NFSSHARP_NFS_SERVER` | `127.0.0.1` | NFS server address. |
| `NFSSHARP_NFS_EXPORT` | `/export` | NFSv3 export path. |
| `NFSSHARP_NFS_EXPECTED_EXPORT_GROUP` | `*` only when server and export use implicit defaults; otherwise unset | Optional access group to assert in mountd export-list results. Leave unset for external servers where the advertised group is server-specific or empty. |
| `NFSSHARP_NFS_UID` | `0` | AUTH_SYS user ID. |
| `NFSSHARP_NFS_GID` | `0` | AUTH_SYS primary group ID. |

The image uses Ubuntu 24.04 pinned by OCI digest and installs the Ubuntu package versions of NFS-Ganesha, its in-memory FSAL, and rpcbind. The container health check verifies portmapper v2, mount protocol v3, and NFS protocol v3 before tests run.
