# API Overview

NfsSharp is split into small packages so applications can depend on the layer they need.

| Package | Main area |
| --- | --- |
| [`NfsSharp.Client`](https://www.nuget.org/packages/NfsSharp.Client) | High-level NFSv3 facade, direct NFSv3 client APIs, and experimental direct NFSv4 COMPOUND APIs. |
| [`NfsSharp.Protocol`](https://www.nuget.org/packages/NfsSharp.Protocol) | XDR primitives, ONC RPC helpers, NFSv3/NFSv4 models, status codes, and RPCSEC_GSS abstractions. |

The recommended entry point for applications is `NfsSharp.Client.NfsClient`, configured with `NfsClientBuilder`. Use `NfsV3Client` when you need direct mounted-client operations such as explicit handles, offset-based reads and writes, COMMIT, FSSTAT, FSINFO, PATHCONF, ACCESS, guarded `SETATTR`, or write/commit verifier inspection.

## Client layer

`NfsSharp.Client` contains:

| Type | Purpose |
| --- | --- |
| `NfsClientBuilder` | Fluent configuration for credentials, privileged source ports, timeouts, retry behavior, bounded RPC concurrency, transfer sizes, directory caching, TCP options, logging, and RPCSEC_GSS hooks. |
| `NfsClient` | Stateful convenience facade for connect, export listing, mount, directory traversal, file I/O, metadata, links, mutations, and unmount. |
| `NfsV3Client` | Direct NFSv3 mounted client for export-relative and file-handle-oriented operations. |
| `NfsV4Client` | Experimental direct COMPOUND-oriented NFSv4.0, NFSv4.1, and NFSv4.2 surface. |

NFSv3 over TCP is the primary supported protocol. The NFSv4 surface is experimental and does not yet carry the same compatibility and integration guarantees.
The experimental `NfsV4Client` currently sends AUTH_SYS only; connecting it with RPCSEC_GSS options is rejected before network access.

NFSv3 transient transport retries are conservative: automatic reconnect/retry is limited to retry-safe discovery, mount negotiation, read-only NFS procedures, and `COMMIT`. Mutating procedures such as `SETATTR`, `WRITE`, `CREATE`, `REMOVE`, `RENAME`, and link creation surface the transport failure to the caller instead of being replayed automatically.

`NfsClientOptions.MaxRetries` sets the number of retries after the initial attempt. `RetryDelay` is a fixed delay between attempts, and caller cancellation interrupts that delay. A retry-safe procedure is sent again on a fresh connection after a transient transport failure; the RPC attempt receives a new XID. The client does not use exponential backoff, and applications should apply their own higher-level limits when coordinating repeated operations.

AUTH_SYS and RPCSEC_GSS NFSv3 calls can be outstanding concurrently on one TCP connection. The local bound defaults to 32 and can be changed with `NfsClientOptions.MaxOutstandingRpcCallsPerConnection` or `NfsClientBuilder.WithMaxOutstandingRpcCallsPerConnection`; RPCSEC_GSS also honors the server's negotiated `seq_window`. Replies are matched by XID and verified against their own attempt record. Operations on a GSS mechanism are serialized while calls are being protected or verified.

### RPCSEC_GSS security limits

RPCSEC_GSS negotiation and mechanism extension points are available (`WithKerberos`, `WithGssMechanism`, `IRpcSecGssMechanism`, `IRpcSecGssQopMechanism`, `IRpcSecGssPrivacyMechanism`). Context establishment validates the final `seq_window` verifier. Data replies fail closed on invalid or missing RPCSEC_GSS verifiers, and integrity/privacy bodies are verified or unwrapped with the exact per-attempt sequence number and QOP before any procedure result is exposed. Unmount sends a best-effort authenticated `RPCSEC_GSS_DESTROY` before closing the NFSv3 connection; Kerberos mechanisms are created per mounted session and disposed with that session.

If the server rejects a request with `RPCSEC_GSS_CREDPROBLEM` or `RPCSEC_GSS_CTXPROBLEM`, the current session is invalidated and the failure is surfaced with its distinct `auth_stat`. The client does not transparently replay the operation or recreate the context because the configured mechanism is a caller-owned instance without a reset/factory contract; create a new NFS client to establish a fresh context before retrying.

The following remain incomplete and **experimental**:

- `WithKerberos` uses the experimental Linux MIT GSSAPI mechanism and the process credential cache. Explicit `GssCredentials`, other platforms, and real-server interoperability evidence are not supported yet. A custom mechanism must provide correct MIC and wrap/unwrap behavior; privacy mechanisms must also report the returned QOP and confidentiality state.
- `NegotiateGssMechanism` performs Kerberos/NTLM token exchange only. .NET `NegotiateAuthentication` does not expose `GSS_GetMIC`/`GSS_VerifyMIC`, so it cannot seal the mandatory data-call header verifier and is rejected for every service, including `rpc_gss_svc_none`. `RpcSecGssMechanism.CanComputeMic` and `ProvidesCryptographicProtection` both report it as false. It is not an end-to-end security guarantee.
- `NoOpGssMechanism` provides no cryptographic protection and is rejected by client configuration for every service; it is only a deterministic protocol-fixture mechanism and is never interoperability evidence.

Applications must not treat the current RPCSEC_GSS surface as an end-to-end Kerberos security guarantee.

## NFSv3 operation groups

`NfsClient` and `NfsV3Client` expose the same core NFSv3 capabilities at different abstraction levels:

| Area | Common APIs |
| --- | --- |
| Discovery and lifecycle | `GetExportedDevicesAsync`, `MountDeviceAsync`, `UnMountDeviceAsync`, `NfsV3Client.ListExportsAsync`, `NfsV3Client.ConnectAsync`, `UnmountAsync` |
| Path and metadata | `LookupAsync`, `GetItemAttributesAsync`, `GetAttributesAsync`, `FileExistsAsync`, `IsDirectoryAsync` |
| Directory traversal | `GetItemListAsync`, `ReadDirAsync`, `ReadDirPlusAsync` |
| File I/O | `ReadAsync`, `ReadAtAsync`, `WriteAsync`, `WriteSafelyAsync`, `WriteAtAsync`, `WriteAtWithResultAsync` |
| Mutation | `CreateFileAsync`, `CreateDirectoryAsync`, `DeleteFileAsync`, `DeleteDirectoryAsync`, `MoveAsync`, `CreateSymLinkAsync`, `CreateHardLinkAsync` |
| Attributes | `ChmodAsync`, `ChownAsync`, `UtimesAsync`, `SetFileSizeAsync`, `SetAttributesAsync`, `SetAttributesGuardedAsync` |
| Capabilities and persistence | `AccessAsync`, `ReadLinkAsync`, `CommitAsync`, `CommitWithResultAsync`, `GetFileSystemStatAsync`, `GetFileSystemInfoAsync`, `GetPathConfAsync` |

`WriteAtWithResultAsync` returns `NfsWriteResult`, including the byte count accepted by the server, the committed stability mode reported by NFS, and the write verifier. `CommitWithResultAsync` returns `NfsCommitResult`, including the commit verifier. Result types validate verifier shape and return defensive copies of verifier bytes. The simpler `WriteAtAsync` and `CommitAsync` overloads remain available when callers do not need those protocol details.

Stream reads require a writable output stream, and stream writes require a readable input stream. Path-based local-file reads validate the remote source before creating the local output path. `WriteAsync` preserves its create-or-truncate behavior and can leave partial content after a failed transfer. `WriteSafelyAsync` stages bytes in a `0600` sibling file, applies the existing destination mode (or the default file mode for a new path), commits staged data when any WRITE reply is less than `FILE_SYNC`, and renames it into place only after the full stream succeeds. It checks WRITE and COMMIT verifiers and aborts publication if they change. A failed transfer before RENAME leaves the old destination intact and attempts to remove the temporary file. Replacing an existing destination depends on the server's NFSv3 RENAME behavior; a lost RENAME reply can make the publish outcome ambiguous, and cleanup can fail if the server remains unreachable. Both path-based write APIs validate the input stream before creating or truncating remote files and return an `NfsLookup` with attributes refreshed after transfer when available.

`SetAttributesGuardedAsync` uses the NFSv3 `ctime` guard so an attribute update only succeeds if the server-side file change time still matches the value previously observed by the caller. Pass `NfsFattr.CtimeTimestamp` to this guard because it preserves raw NFS nanosecond precision. `NfsFattr` also exposes `AtimeTimestamp` and `MtimeTimestamp` with the same precision. Decoded attributes always carry all three timestamps, including a valid Unix epoch value of `(0, 0)`; optional attributes are represented by a null attribute record (`post_op_attr` / `name_attributes`), never by null timestamps.

## Protocol layer

`NfsSharp.Protocol` contains reusable protocol contracts and primitives, including:

- XDR encoding and decoding helpers.
- NFSv3 and NFSv4 model types.
- NFS status codes and exception mapping.
- AUTH_SYS options shared by client APIs.
- RPCSEC_GSS extension contracts for authentication, integrity, and privacy work. Context and data-reply verifiers fail closed, and protected bodies are checked before result decoding; real Kerberos interoperability remains experimental (see Client layer notes above).

Use the protocol package directly for protocol tooling, tests, or integrations that do not need the high-level client package.
