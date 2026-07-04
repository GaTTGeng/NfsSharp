# NFS Compatibility

NfsSharp provides managed NFS protocol and client APIs for .NET. NFSv3 over TCP is the primary target. NFSv4.0, NFSv4.1, and NFSv4.2 APIs are experimental and do not yet carry the same compatibility guarantees.

See the [roadmap](roadmap.md) for delivery order and the [GitHub milestones](https://github.com/GaTTGeng/NfsSharp/milestones) for live progress.

## Compatibility Contract

NfsSharp considers a behavior supported when it:

- works through a documented managed API;
- is covered by a focused automated test;
- is exercised against a real server when the behavior depends on interoperability;
- behaves consistently across supported .NET targets;
- matches the relevant NFS, XDR, and ONC RPC semantics for results, state, and failures.

An implemented method without real-server verification remains partial or experimental.

## Status Legend

| Status | Meaning |
| --- | --- |
| **Supported** | Covered for the stated environment with no known major semantic gap. |
| **Partial** | A useful implementation exists, but integration or edge-case coverage is incomplete. |
| **Experimental** | Public surface exists, but behavior and API stability need substantial validation. |
| **Planned** | Work is accepted into the roadmap but is not yet implemented or verified. |
| **Research** | The correct compatibility or SDK contract must be validated before implementation. |

## Capability Snapshot

| Area | Current level | Track |
| --- | --- | --- |
| XDR primitives used by current protocol code | **Partial** | M2 |
| ONC RPC record marking over TCP | **Partial** | M2/M3 |
| AUTH_SYS credentials | **Partial** | M1/M2 |
| Portmapper, export discovery, mount, and unmount | **Partial** | M1/M2 |
| NFSv3 metadata and path lookup | **Partial** | M1/M2 |
| NFSv3 directory enumeration | **Partial** | M1/M2 |
| NFSv3 file read, write, and commit | **Partial** | M1/M2/M3 |
| NFSv3 create, remove, rename, link, and attribute mutation | **Partial** | M1/M2 |
| RPCSEC_GSS and Kerberos | **Experimental** | M4 |
| NFSv4.0 COMPOUND operations | **Experimental** | M5 |
| NFSv4.1 sessions and stateful operations | **Experimental** | M5 |
| NFSv4.2 SEEK, ALLOCATE, DEALLOCATE, COPY, and CLONE | **Experimental** | M5 |
| Multi-server interoperability matrix | **Planned** | M1/M7 |
| IPv6 and additional transport support | **Research** | M7 |

## NFSv3 Behavior Matrix

| Protocol area | NfsSharp API | Status | Track | Remaining work |
| --- | --- | --- | --- | --- |
| Export discovery | `NfsV3Client.ListExportsAsync`, `NfsClient.GetExportedDevicesAsync` | **Partial** | M1/M2 | Real-server coverage, group parsing, empty lists, denial, and portmapper variations. |
| Mount and unmount | `NfsV3Client.ConnectAsync`, `NfsV3Client.UnmountAsync`, `NfsClient.MountDeviceAsync`, `NfsClient.UnMountDeviceAsync` | **Partial** | M1/M2/M3 | Covered by repository NFS-Ganesha integration tests for repeated mount/unmount, remount replacement, invalid mount failure state, idempotent facade cleanup, and direct-client unmount idempotence. Remaining work: cleanup after transport failures, reconnect behavior, and server restart cases. |
| Path lookup and attributes | `LookupPathAsync`, `GetAttributesAsync` | **Partial** | M1/M2 | Verified on the repository NFS-Ganesha server for root, nested, missing, file, directory, and symbolic-link paths, including handle/path attribute consistency, mode, size, file identifiers, timestamps, traversal rejection, and `NOENT`/`NOTDIR` status preservation. Remaining work: stale handles, broader timestamp precision behavior, and cross-server attribute fidelity. |
| Access checks and links | `AccessAsync`, `ReadLinkAsync`, link creation APIs | **Partial** | M1/M2 | Verified on the repository NFS-Ganesha server for file and directory ACCESS masks, zero-mask handling, invalid mask rejection, symbolic-link target reads, hard-link identity, and restricted-directory denial when mode bits are enforced. Remaining work: server policy differences, link limits, symlink loops, and broader error mapping. |
| Directory enumeration | `ReadDirAsync`, `ReadDirPlusAsync` | **Partial** | M1/M2/M3 | Verified on the repository NFS-Ganesha server for empty, small, nested, and multi-page directories, including `READDIRPLUS` attributes and handles. Remaining work: mutation during enumeration, larger cross-server directories, and fault recovery. |
| File reads | `ReadAtAsync`, `ReadFileAsync` | **Partial** | M1/M2/M3 | Verified on the repository NFS-Ganesha server for empty, small, unaligned, boundary-offset, and multi-chunk reads, including EOF reporting, streamed byte fidelity, advertised read-size inspection, configured read chunks, cancellation, invalid handles, output stream writability validation, local-output creation only after remote source validation, buffer range validation, zero-length reads, and missing paths. Remaining work: larger cross-server files, retry recovery, and server-specific short-read behavior. |
| File writes and commit | `WriteAtAsync`, `WriteAtWithResultAsync`, `WriteFileAsync`, `CommitAsync`, `CommitWithResultAsync` | **Partial** | M1/M2/M3 | Covered by repository tests for offset writes, sparse extension reads, streamed writes, readable input validation before path create/truncate, refreshed path-write return attributes, configured small write chunks, explicit commit by file handle/path, write and commit verifier exposure, result verifier defensive copying and validation, configured stability-mode requests, zero-length write no-ops, configured write-size guards, and facade forwarding. Remaining work: verifier-change behavior across server restarts, partial-write edge cases, retries, commit ranges, and recovery. |
| Create and remove | `CreateFileAsync`, `CreateDirectoryAsync`, deletion APIs | **Partial** | M1/M2 | Verified on the repository NFS-Ganesha server for file and directory creation with resulting type/mode attributes, file deletion, empty-directory deletion, non-empty-directory `NOTEMPTY`, and recursive directory cleanup. Remaining work: races, stale handles, and broader server-specific failures. |
| Rename and links | `MoveAsync`, `CreateSymLinkAsync`, `CreateHardLinkAsync` | **Partial** | M1/M2 | Verified on the repository NFS-Ganesha server for same-directory rename, cross-directory rename, invalid target parent `NOTDIR`, symbolic-link target preservation, and hard-link identity/content survival after original removal. Replacement rename is covered, with the repository MEM server currently returning `IO` and preserving both files instead of replacing the target. Remaining work: cross-filesystem errors, symlink loops, link-limit failures, races, and broader server policy differences. |
| Attribute mutation | `SetAttributesAsync`, `SetAttributesGuardedAsync`, `ChmodAsync`, `ChownAsync`, `UtimesAsync`, `SetFileSizeAsync` | **Partial** | M1/M2 | Covered by repository NFS-Ganesha integration tests for mode, size truncation/extension, timestamp changes, guarded `ctime` success and stale-guard `NOT_SYNC` failure through path, file-handle, and facade APIs. Remaining work: identity mapping, permission failures, and broader timestamp precision behavior. |
| File-system capabilities | `GetFileSystemStatAsync`, `GetFileSystemInfoAsync`, `GetPathConfAsync` | **Partial** | M1/M2 | Verified on the repository NFS-Ganesha server for path and file-handle overloads, FSSTAT capacity/file-count invariants including zero-valued unavailable MEM FSAL fields, FSINFO transfer-size, maximum-file-size, time-delta, and property flags, plus PATHCONF name limits, zero-valued unavailable link limits, and case behavior. Remaining work: additional unavailable/server-specific fields, broader overflow boundaries, and cross-server matrix coverage. |
| Timeouts and retries | `NfsClientOptions` | **Partial** | M3 | Covered by unit tests for invalid command-timeout, retry-count, and retry-delay option rejection, retry-safe procedure classification, plus repository NFS-Ganesha integration coverage for reconnecting and retrying after a disposed transport connection. Automatic retries are limited to retry-safe discovery, mount negotiation, read-only NFS procedures, and `COMMIT`; mutating procedures surface the transient failure instead of being replayed automatically. Active NFS connections are refreshed after command timeouts so later calls do not reuse a timed-out RPC stream. Remaining work: deterministic fault injection, cancellation precedence, server restart behavior, and backoff policy. |
| Directory cache | `NfsClientOptions.EnableDirectoryCache` | **Partial** | M3 | Covered by unit tests for invalid cache TTL rejection and by repository NFS-Ganesha integration tests for same-client `READDIRPLUS` attribute refresh after file-size mutations, stream rewrites, create/rename/delete invalidation, defensive result copying, TTL expiry, and cross-client mutation visibility after expiry. Remaining work: expiry races, concurrent mutation behavior, and broader invalidation policy coverage. |

## NFSv4 Boundary

`NfsV4Client` exposes direct COMPOUND-oriented APIs and selected convenience operations. These APIs are experimental because the current automated suite does not validate server interoperability, state recovery, lease behavior, replay handling, callbacks, or a broad security matrix.

The high-level `NfsClient` facade intentionally remains NFSv3-only until NFSv4 lifecycle and recovery guarantees are strong enough to define a stable abstraction.

## Protocol References

- [RFC 4506: External Data Representation Standard](https://www.rfc-editor.org/rfc/rfc4506)
- [RFC 5531: Remote Procedure Call Protocol Version 2](https://www.rfc-editor.org/rfc/rfc5531)
- [RFC 1813: NFS Version 3 Protocol Specification](https://www.rfc-editor.org/rfc/rfc1813)
- [RFC 2203: RPCSEC_GSS Protocol Specification](https://www.rfc-editor.org/rfc/rfc2203)
- [RFC 7530: Network File System Version 4 Protocol](https://www.rfc-editor.org/rfc/rfc7530)
- [RFC 8881: Network File System Version 4 Minor Version 1 Protocol](https://www.rfc-editor.org/rfc/rfc8881)
- [RFC 7862: Network File System Version 4 Minor Version 2 Protocol](https://www.rfc-editor.org/rfc/rfc7862)

## Verification Approach

Compatibility work should use the smallest useful test:

1. Unit tests for XDR encoding, decoding, validation, status mapping, and deterministic helpers.
2. Protocol fixture tests for RPC requests and responses, including malformed and boundary payloads.
3. Real-server integration tests for observable NFS behavior and lifecycle.
4. Fault-injection tests for timeout, disconnect, retry, restart, stale-handle, and partial-I/O behavior.
5. A documented server matrix for behavior known to vary across implementations.

Packet normalization may remove transport identifiers or other non-semantic values. It must not hide procedure arguments, status codes, attributes, verifier changes, or ordering that callers can observe.

## Reporting a Gap

[Open an NFS compatibility issue](https://github.com/GaTTGeng/NfsSharp/issues/new?template=nfs_compatibility_gap.yml) and include:

- NfsSharp package and version;
- NFS protocol version and authentication mode;
- server implementation and version;
- the smallest code and server setup that reproduce the behavior;
- expected or reference behavior and actual NfsSharp behavior;
- sanitized logs, packet traces, or RFC references when available.
