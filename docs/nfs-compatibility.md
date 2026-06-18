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
| Mount and unmount | `NfsV3Client.ConnectAsync`, `NfsV3Client.UnmountAsync` | **Partial** | M1/M2/M3 | Lifecycle tests, cleanup after failures, reconnect behavior, and server restart cases. |
| Path lookup and attributes | `LookupPathAsync`, `GetAttributesAsync` | **Partial** | M1/M2 | Path edge cases, stale handles, symlinks, permission failures, and attribute fidelity. |
| Access checks and links | `AccessAsync`, `ReadLinkAsync`, link creation APIs | **Partial** | M1/M2 | Server policy differences, link limits, loops, and error mapping. |
| Directory enumeration | `ReadDirAsync`, `ReadDirPlusAsync` | **Partial** | M1/M2/M3 | Cookie verifier handling, pagination, mutation during enumeration, and large directories. |
| File reads | `ReadAtAsync`, `ReadFileAsync` | **Partial** | M1/M2/M3 | Short reads, EOF boundaries, negotiated sizes, cancellation, large files, and recovery. |
| File writes and commit | `WriteAtAsync`, `WriteFileAsync`, `CommitAsync` | **Partial** | M1/M2/M3 | Partial writes, verifier changes, stability modes, retries, commit ranges, and recovery. |
| Create and remove | `CreateFileAsync`, `CreateDirectoryAsync`, deletion APIs | **Partial** | M1/M2 | Create modes, attributes, non-empty directories, races, and server-specific failures. |
| Rename and hard links | `MoveAsync`, `CreateHardLinkAsync` | **Partial** | M1/M2 | Cross-directory behavior, cross-filesystem errors, replacement rules, and races. |
| Attribute mutation | `SetAttributesAsync`, `ChmodAsync`, `ChownAsync`, `UtimesAsync`, `SetFileSizeAsync` | **Partial** | M1/M2 | Guard checks, timestamp precision, identity mapping, truncation, and permission failures. |
| File-system capabilities | `GetFileSystemStatAsync`, `GetFileSystemInfoAsync`, `GetPathConfAsync` | **Partial** | M1/M2 | Field fidelity, overflow boundaries, unsupported values, and server matrix coverage. |
| Timeouts and retries | `NfsClientOptions` | **Partial** | M3 | Deterministic fault injection, cancellation precedence, duplicate request safety, and backoff policy. |
| Directory cache | `NfsClientOptions.EnableDirectoryCache` | **Partial** | M3 | Invalidation semantics, mutation coherence, expiry races, and concurrency coverage. |

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
