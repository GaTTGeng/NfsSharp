# NfsSharp Roadmap

NfsSharp is evolving toward a dependable managed NFS SDK for .NET applications that need direct protocol access without invoking native NFS command-line tools.

> The roadmap describes direction, not a release guarantee. GitHub milestones are the source of truth for current scope and issue progress.

## Current Focus

**M1: NFSv3 Integration Baseline** is the active workstream. The immediate goal is a repeatable integration harness backed by a real NFSv3 server, followed by focused tests for export discovery, mount and unmount, metadata, directory traversal, file I/O, mutations, and cleanup.

Track the live work in [M1 on GitHub](https://github.com/GaTTGeng/NfsSharp/milestone/1) or review the detailed [compatibility matrix](nfs-compatibility.md).

## Delivery Principles

- Prefer managed .NET protocol behavior over invoking native NFS tools at runtime.
- Verify behavior against real servers with small, reproducible fixtures.
- Treat wire encoding, authentication, file mutation, and retry behavior as correctness-sensitive work.
- Prioritize NFSv3 interoperability and production reliability before expanding the high-level API to NFSv4.
- Keep public APIs stable, documented, cancellable, and useful independently of the high-level facade.
- Do not claim compatibility based only on the presence of an implementation.

## Milestone Plan

| Phase | Status | Outcome |
| --- | --- | --- |
| [M1: NFSv3 Integration Baseline](https://github.com/GaTTGeng/NfsSharp/milestone/1) | **Active** | Repeatable real-server tests for common NFSv3 discovery, mount, metadata, directory, I/O, mutation, and teardown workflows. |
| [M2: NFSv3 Protocol Conformance](https://github.com/GaTTGeng/NfsSharp/milestone/2) | Planned | Verified XDR, ONC RPC, portmapper, mount protocol, and NFSv3 procedure semantics with focused error and edge-case coverage. |
| [M3: Reliability and Production I/O](https://github.com/GaTTGeng/NfsSharp/milestone/3) | Planned | Predictable timeout, cancellation, retry, reconnect, partial I/O, stable write, large file, concurrency, cache, and stale-handle behavior. |
| [M4: RPCSEC_GSS and Kerberos](https://github.com/GaTTGeng/NfsSharp/milestone/4) | Planned | Interoperable authentication, integrity, and privacy flows with secure credential lifecycle and failure handling. |
| [M5: NFSv4 Stabilization](https://github.com/GaTTGeng/NfsSharp/milestone/5) | Planned | Dedicated NFSv4.0, NFSv4.1, and NFSv4.2 integration coverage for COMPOUND, state, sessions, recovery, security, and advanced operations. |
| [M6: Public SDK Hardening](https://github.com/GaTTGeng/NfsSharp/milestone/6) | Ongoing | Stronger API documentation, examples, diagnostics, analyzers, nullable correctness, package quality, and compatibility guidance. |
| [M7: Compatibility Expansion Research](https://github.com/GaTTGeng/NfsSharp/milestone/7) | Research | Evidence-based decisions on additional servers, IPv6 and transports, target frameworks, performance limits, and broader support guarantees. |

## How Work Advances

A compatibility item is ready to leave a milestone when:

1. The intended behavior is captured by a focused unit, integration, interoperability, or fault-injection test.
2. The test identifies the protocol version, authentication mode, and server implementation it exercises.
3. Supported behavior and remaining gaps are reflected in the compatibility matrix.
4. Public API changes include XML documentation and a usage example when appropriate.
5. Release builds and tests pass for the repository's supported .NET targets.

Milestones may overlap where behavior crosses subsystem boundaries. For example, stable writes depend on NFSv3 procedure correctness in M2 and retry and recovery semantics in M3.

## Contributing

Start with an existing milestone issue where possible. For a newly discovered interoperability difference, [open an NFS compatibility issue](https://github.com/GaTTGeng/NfsSharp/issues/new?template=nfs_compatibility_gap.yml) and include a minimal reproduction, NfsSharp version, protocol version, server implementation, expected behavior, actual behavior, and sanitized logs or packet evidence.
