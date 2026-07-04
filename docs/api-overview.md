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
| `NfsClientBuilder` | Fluent configuration for credentials, privileged source ports, timeouts, retry behavior, transfer sizes, directory caching, TCP options, logging, and RPCSEC_GSS hooks. |
| `NfsClient` | Stateful convenience facade for connect, export listing, mount, directory traversal, file I/O, metadata, links, mutations, and unmount. |
| `NfsV3Client` | Direct NFSv3 mounted client for export-relative and file-handle-oriented operations. |
| `NfsV4Client` | Experimental direct COMPOUND-oriented NFSv4.0, NFSv4.1, and NFSv4.2 surface. |

NFSv3 over TCP is the primary supported protocol. The NFSv4 surface is experimental and does not yet carry the same compatibility and integration guarantees.

NFSv3 transient transport retries are conservative: automatic reconnect/retry is limited to retry-safe discovery, mount negotiation, read-only NFS procedures, and `COMMIT`. Mutating procedures such as `SETATTR`, `WRITE`, `CREATE`, `REMOVE`, `RENAME`, and link creation surface the transport failure to the caller instead of being replayed automatically.

## NFSv3 operation groups

`NfsClient` and `NfsV3Client` expose the same core NFSv3 capabilities at different abstraction levels:

| Area | Common APIs |
| --- | --- |
| Discovery and lifecycle | `GetExportedDevicesAsync`, `MountDeviceAsync`, `UnMountDeviceAsync`, `NfsV3Client.ListExportsAsync`, `NfsV3Client.ConnectAsync`, `UnmountAsync` |
| Path and metadata | `LookupAsync`, `GetItemAttributesAsync`, `GetAttributesAsync`, `FileExistsAsync`, `IsDirectoryAsync` |
| Directory traversal | `GetItemListAsync`, `ReadDirAsync`, `ReadDirPlusAsync` |
| File I/O | `ReadAsync`, `ReadAtAsync`, `WriteAsync`, `WriteAtAsync`, `WriteAtWithResultAsync` |
| Mutation | `CreateFileAsync`, `CreateDirectoryAsync`, `DeleteFileAsync`, `DeleteDirectoryAsync`, `MoveAsync`, `CreateSymLinkAsync`, `CreateHardLinkAsync` |
| Attributes | `ChmodAsync`, `ChownAsync`, `UtimesAsync`, `SetFileSizeAsync`, `SetAttributesAsync`, `SetAttributesGuardedAsync` |
| Capabilities and persistence | `AccessAsync`, `ReadLinkAsync`, `CommitAsync`, `CommitWithResultAsync`, `GetFileSystemStatAsync`, `GetFileSystemInfoAsync`, `GetPathConfAsync` |

`WriteAtWithResultAsync` returns `NfsWriteResult`, including the byte count accepted by the server, the committed stability mode reported by NFS, and the write verifier. `CommitWithResultAsync` returns `NfsCommitResult`, including the commit verifier. Result types validate verifier shape and return defensive copies of verifier bytes. The simpler `WriteAtAsync` and `CommitAsync` overloads remain available when callers do not need those protocol details.

Stream reads require a writable output stream, and stream writes require a readable input stream. Path-based stream writes validate the input stream before creating or truncating the remote file. They return an `NfsLookup` for the written file; its attributes are refreshed after the stream write completes, so `Attr.Size` reflects the final server-side length when the server returns post-write attributes normally.

`SetAttributesGuardedAsync` uses the NFSv3 `ctime` guard so an attribute update only succeeds if the server-side file change time still matches the value previously observed by the caller. Prefer `NfsFattr.CtimeTimestamp` over `Ctime` for this guard because it preserves raw NFS nanosecond precision.

## Protocol layer

`NfsSharp.Protocol` contains reusable protocol contracts and primitives, including:

- XDR encoding and decoding helpers.
- NFSv3 and NFSv4 model types.
- NFS status codes and exception mapping.
- AUTH_SYS options shared by client APIs.
- RPCSEC_GSS extension contracts for authentication, integrity, and privacy work.

Use the protocol package directly for protocol tooling, tests, or integrations that do not need the high-level client package.
