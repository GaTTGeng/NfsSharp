# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Added bounded concurrent AUTH_SYS RPC calls over one NFSv3 TCP connection, dispatched by XID through a single receive loop; RPCSEC_GSS calls remain serialized.
- Added `NfsClientOptions.MaxOutstandingRpcCallsPerConnection` and `NfsClientBuilder.WithMaxOutstandingRpcCallsPerConnection`, defaulting to 32 outstanding calls.
- Added immutable per-attempt RPCSEC_GSS security records (`RpcSecGssCallRecord`) carrying XID, sequence number, service, QOP, context generation, and procedure identity. Sequence numbers are allocated once per transmitted attempt, including retries.
- Added `IRpcSecGssQopMechanism` for QOP-aware MIC and wrap/unwrap operations, and `RpcSecGssMechanism` helpers for QOP dispatch and cryptographic-strength classification.
- Added `NfsFattr.AtimeTimestamp` and `NfsFattr.MtimeTimestamp` alongside `CtimeTimestamp` so raw NFS nanosecond precision is available for every fattr3 timestamp, including Unix epoch zero.

### Changed

- Per-call timeouts and cancellations remove only their pending RPC; a canceled partial write or stalled partial reply retires the shared connection so later calls can reconnect safely. Fatal receive errors also trigger reconnection before a later call.
- Corrected RPCSEC_GSS `rpc_gss_proc_t` discriminators to RFC 2203 values (`DATA=0`, `INIT=1`, `CONTINUE_INIT=2`, `DESTROY=3`) and encoded `rpc_gss_cred_t` as version / gss_proc / seq_num / service / handle. Obsolete aliases `Create`, `GetMic`, and `Wrap` remain for source compatibility.
- RPCSEC_GSS request verifiers are now GSS MICs over the RPC header up to and including the credential (RFC 2203 section 5.3.1).
- Documented that RPCSEC_GSS negotiation hooks are available while full reply verification, integrity, and privacy remain incomplete and experimental; applications must not treat the surface as an end-to-end Kerberos security guarantee.

### Fixed

- Preserved NFSv3 Unix epoch timestamps (`nfstime3` seconds=0, nanoseconds=0) instead of treating them as absent values. Absent optional attributes are represented only through `post_op_attr` / `name_attributes` presence flags.
- RPCSEC_GSS data-reply verifiers are now validated fail-closed against the network-order request sequence number using the request QOP before any procedure result is exposed. Missing, wrong-flavor, empty, mismatched, or unverifiable MICs are rejected, including on accepted RPC errors. Replay against a replaced context generation is rejected.
- `NegotiateGssMechanism` no longer pretends to compute or verify MICs through the Negotiate handshake API. Those operations fail closed until a real GSS integrity API is wired, so unbound verifiers cannot be accepted. `RpcSecGssMechanism.CanComputeMic` and `ProvidesCryptographicProtection` report it as false, and configuration is rejected for every service (including `rpc_gss_svc_none`) because the mandatory data-call header MIC cannot be produced.
- Accept server-selected RPCSEC_GSS `seq_window` values above the local default of 64; only a zero window is rejected (RFC 2203 does not cap the negotiated window).
- `NoOpGssMechanism` emits a deterministic nonempty verifier so the permitted `rpc_gss_svc_none` data-call path stays usable.

### Security

- `NoOpGssMechanism` is rejected when integrity or privacy service is requested; it remains deterministic-test-only and is never interoperability evidence.

## [1.2.0] - 2026-08-13

### Added

- Added public `RpcAuthSys`, `RpcReply`, `RpcReplyParser`, and `MountV3Status` protocol helpers, plus bounded `XdrReader.Opaque(int)` and `SkipOpaque(int)` overloads.
- Added an Ubuntu 24.04 Linux kernel NFSv3 integration fixture and CI job as a second-server interoperability baseline, including retained server diagnostics and cross-server compatibility documentation.

### Changed

- Reconciled the public roadmap with completed M1 and M2 delivery evidence and made M3 reliability and production I/O the current implementation focus without broadening the documented NFS compatibility contract.

### Fixed

- Validated NFSv3 mutation, durability, and capability procedure result arms with status-preserving fixtures, and rejected malformed FSINFO `time_delta` nanoseconds values.
- Hardened NFSv3 read-side result validation by rejecting ACCESS grants outside the requested mask, bounding READ response data to the requested size, rejecting non-terminal empty READ responses, and surfacing unsupported `fattr3` file sizes as protocol exceptions.
- Validated complete ONC RPC reply envelopes, including XID correlation, accepted and denied failure variants, reply verifier bounds, and invalid discriminators before exposing a procedure result.
- Hardened NFSv3 RPC record decoding for fragmented and truncated records, and rejected non-zero XDR padding bytes.
- Made NFSv3 portmapper discovery fail explicitly for unavailable or invalid TCP mappings instead of falling back to an implicit NFS port, and preserved RPC program/version/procedure rejection context and mount status names in public exceptions.
- Made NFSv3 unmount transport failures observable while still closing the local NFS connection and retaining idempotent repeated unmount behavior.

### Dependencies

- Updated `Microsoft.Extensions.Logging.Abstractions` to 10.0.11 and `Microsoft.SourceLink.GitHub` to 10.0.400.
- Updated `actions/setup-dotnet` to v6 for build and release workflows.

## [1.1.2] - 2026-07-21

### Fixed

- Ensured NFSv3 direct-client and facade write operations honor an already-cancelled token before validating connection or mount state or performing network work.
- Reject non-terminal NFSv3 directory pages that do not advance their enumeration cookie, preventing malformed responses from causing an infinite loop.
- Limit the total size of multi-fragment ONC RPC records to 64 MiB for NFSv3 and NFSv4 clients.

## [1.1.1] - 2026-07-08

### Fixed

- Corrected NFSv4 `nfsstat4` constants and descriptions used in protocol exceptions.
- Corrected NFSv4 COMPOUND response decoding to read `status`, `tag`, and operation results in wire order, preserve operation payloads, and decode `fattr4` attribute lists as XDR opaque data.
- Corrected NFSv4 `OPEN4_NOCREATE` argument encoding by removing create-only fields from open-existing requests.
- Corrected NFSv4.2 `COPY` and `CLONE` request construction, including source/destination filehandle order, COPY argument layout, CLONE opcode use, and COPY response payload capture.
- Corrected NFSv4 `SECINFO` path handling so the operation resolves the parent directory and sends only the target name.
- Corrected NFSv4 `SECINFO` RPCSEC_GSS response decoding to treat `sec_oid4` as a single XDR opaque value.
- Corrected NFSv4.1+ `OPEN_DELEGATE_NONE_EXT` response capture so legal OPEN responses with extended no-delegation reasons are accepted.
- Hardened XDR boolean decoding to reject malformed values other than `0` or `1`.

## [1.1.0] - 2026-07-06

### Added

- Repeatable NFSv3 integration harness backed by an NFS-Ganesha test server.
- Deterministic NFSv3 fixture materialization for directory, file, link, permission, and boundary-file scenarios.
- Real-server coverage for NFSv3 export discovery, mount/unmount lifecycle, metadata lookup, ACCESS, READLINK, READDIR, READDIRPLUS, file reads, file writes, COMMIT, create/remove, rename, links, attribute mutation, FSSTAT, FSINFO, PATHCONF, directory caching, reconnect, timeout, and facade workflows.
- NFSv3 APIs for write and commit verifier inspection, file-system capability queries, guarded attribute updates, directory cache configuration, socket keepalive/no-delay options, and RPCSEC_GSS extension points.
- Compatibility matrix, roadmap, integration test evidence guide, pull-request checklist, and maintainer release guide.

### Changed

- Hardened NFSv3 stream validation, remote-read validation before local file creation, direct read-size guarding, directory cache invalidation/expiry, timeout recovery, and transient retry policy.
- Limited automatic retries to retry-safe discovery, mount negotiation, read-only NFS procedures, and `COMMIT` so mutating procedures are not replayed after transient transport failures.
- Updated repository CI to build, test, pack, run NFSv3 integration coverage, and upload NuGet/test artifacts.
- Updated NuGet packaging for .NET 8, .NET 9, and .NET 10 with SourceLink, symbols, package README, and Trusted Publishing release workflow.

### Fixed

- Corrected the default RPC machine-name fallback to `nfssharp`.
- Corrected NFSv4 `bitmap4` construction from attribute numbers and `stateid4` encoding/decoding as fixed `seqid + other` fields.

### Dependencies

- Updated GitHub Actions, xUnit runner, coverlet collector, and Microsoft.NET.Test.Sdk dependencies used by the test and CI toolchain.

## [1.0.0] - 2026-06-18

### Added

- Initial managed NFSv3 client and protocol packages.
