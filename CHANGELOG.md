# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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
