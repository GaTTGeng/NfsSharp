# NFSv3 client internal architecture

`NfsV3Client` is the public compatibility facade. Its public API and high-level file,
stream, recursive-directory, and path-based workflows remain in that facade, while
wire and mutable infrastructure state have one internal owner each.

The dependency direction is:

```text
NfsV3Client
  -> NfsPathResolver
  -> NfsV3ProtocolClient -> IRpcCallClient
  -> PortmapClient ------> RpcClient
  -> MountClient --------> RpcClient
                            -> NfsRetryPolicy
                            -> RpcSecGssSession
                            -> RpcTransport -> RpcConnection -> RpcRecordStream
                                                           -> pending XID calls
```

## Ownership boundaries

- `RpcTransport` resolves the server, creates and configures sockets, frames RPC
  records, assigns connection generations, and owns connection shutdown.
- `RpcClient` owns collision-safe XID allocation, bounded in-flight calls, RPC call
  and reply envelopes, timeout precedence, reconnects, and RPC error context.
- `RpcConnection` owns one receive loop, the XID-keyed pending-call table, and a
  send lock held only for complete record writes. Replies for unknown or late XIDs
  are discarded. A failed connection completes all calls tied to that generation.
- `PortmapClient` and `MountClient` own their respective v2/v3 procedure arguments,
  response validation, and mount lifecycle calls.
- `NfsV3ProtocolClient` owns file-handle-based NFSv3 procedure encoding and decoding,
  NFS status mapping, post-operation attributes, and weak cache consistency fields.
- `NfsPathResolver` owns export-relative path normalization, traversal rejection,
  lookup traversal, and parent/name resolution.
- `NfsRetryPolicy` is the single source for transient classification, safe replay
  eligibility, attempt limits, and backoff. Mutation procedures remain non-retryable.
- `RpcSecGssSession` owns security context and per-call security metadata. Full reply
  verifier, integrity, and privacy correctness remain future security work.
- `NfsDirectoryCache` owns cached directory entries, defensive copies, expiry, and
  mutation invalidation.

All extracted types remain internal. `IRpcCallClient` is an internal fixture seam so
NFS procedure bytes and response parsing can be tested without a public client or a
live server. AUTH_SYS calls use bounded XID multiplexing; RPCSEC_GSS remains serialized
until per-request sequence-window and reply-verifier behavior is implemented.
