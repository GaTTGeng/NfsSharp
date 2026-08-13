# NfsSharp Roadmap

NfsSharp is evolving into a dependable managed NFS SDK for .NET applications that need direct protocol access without invoking native NFS tools at runtime. NFSv3 over TCP is the supported product path; the direct NFSv4 APIs remain experimental.

> This document records delivery intent and acceptance criteria, not release dates or a compatibility guarantee. GitHub milestones and issues are the source of truth for work assignment. The [compatibility matrix](nfs-compatibility.md) is the source of truth for supported behavior.

## Current Focus: Start M3 Reliability and Production I/O

**M1: NFSv3 Integration Baseline** and **M2: NFSv3 Protocol Conformance** are complete. Together they established repeatable real-server integration coverage, bounded XDR and ONC RPC decoding, status-preserving protocol fixtures, explicit portmapper and mount failure behavior, and a two-server NFSv3 interoperability baseline.

The next implementation milestone is **M3: Reliability and Production I/O**. It turns the M1/M2 protocol baseline into deterministic recovery behavior for timeouts, cancellation races, disconnects, server restarts, partial I/O, verifier changes, cache concurrency, and stale handles. M6 public-SDK work remains cross-cutting, while M7 compatibility research may inform support decisions without silently expanding the current compatibility contract.

### M2 completion evidence

| Workstream | Issue | Pull request | Completion evidence |
| --- | --- | --- | --- |
| XDR and RPC TCP record limits | [#50](https://github.com/GaTTGeng/NfsSharp/issues/50) | [#58](https://github.com/GaTTGeng/NfsSharp/pull/58) | Byte fixtures cover fragmentation, truncation, non-zero padding, invalid booleans, opaque-length bounds, and the 64 MiB aggregate record ceiling. |
| ONC RPC replies and AUTH_SYS boundaries | [#51](https://github.com/GaTTGeng/NfsSharp/issues/51) | [#59](https://github.com/GaTTGeng/NfsSharp/pull/59) | Fixtures distinguish accepted and denied replies, validate XID/verifier/discriminator ordering, preserve RPC failure context, and cover AUTH_SYS machine-name and auxiliary-group limits. |
| Portmapper and mount variants | [#52](https://github.com/GaTTGeng/NfsSharp/issues/52) | [#60](https://github.com/GaTTGeng/NfsSharp/pull/60) | Fixtures cover unavailable and invalid mappings, RPC rejection context, export-list variants, mount statuses, and observable idempotent unmount cleanup. |
| Read, lookup, and directory result semantics | [#53](https://github.com/GaTTGeng/NfsSharp/issues/53) | [#61](https://github.com/GaTTGeng/NfsSharp/pull/61) | RFC 1813 fixtures cover success/status/optional arms, READ count and EOF consistency, ACCESS masks, file-size bounds, and READDIR/READDIRPLUS continuation and malformed pages. |
| Mutation, durability, and capability result semantics | [#54](https://github.com/GaTTGeng/NfsSharp/issues/54) | [#64](https://github.com/GaTTGeng/NfsSharp/pull/64) | Fixtures preserve mutation statuses and WCC arms, validate WRITE/COMMIT fields and boundaries, and cover FSSTAT/FSINFO/PATHCONF zero-valued, status, and malformed results. |
| Second-server interoperability baseline | [#55](https://github.com/GaTTGeng/NfsSharp/issues/55) | [#65](https://github.com/GaTTGeng/NfsSharp/pull/65) | The shared NFSv3 suite runs against NFS-Ganesha MEM and Ubuntu 24.04 Linux kernel NFS with explicit server-specific expectations and retained failure diagnostics. |

Final M2 validation used NFSv3 over TCP with AUTH_SYS. Release restore, build, and unit tests passed with 92 tests passing and 36 opt-in integration tests skipped; the `Build, test, and pack`, `NFSv3 integration (NFS-Ganesha)`, and `NFSv3 integration (Linux kernel)` jobs passed on the final M2 implementation commit. The [compatibility matrix](nfs-compatibility.md) records the exact covered behavior and retained evidence.

Completing M2 does not promote every tracked capability from partial to supported. UDP, Kerberos/RPCSEC_GSS, recovery and fault injection, identity mapping beyond the isolated AUTH_SYS fixtures, broad production support for all servers, and additional implementations remain assigned to later milestones or explicitly out of scope.

## Delivery Principles

- Prefer managed .NET protocol behavior over invoking native NFS tools at runtime.
- Verify interoperability-dependent behavior against real servers with small, reproducible fixtures.
- Treat XDR, ONC RPC framing, authentication, file mutation, and retry behavior as correctness-sensitive work.
- Do not promote an API from partial or experimental status merely because an implementation exists.
- Keep public APIs stable, documented, cancellable, and useful independently of the high-level facade.
- Make retries opt-in by procedure safety: never automatically replay a mutation unless the protocol semantics and recovery tests prove it safe.
- Keep NFSv4 out of the high-level `NfsClient` facade until its lifecycle and recovery contract is stable.

## Milestone Plan

| Milestone | Status | Outcome | Exit gate |
| --- | --- | --- | --- |
| [M1: NFSv3 Integration Baseline](https://github.com/GaTTGeng/NfsSharp/milestone/1) | Completed | Reproducible NFSv3 real-server baseline and evidence trail. | All M1 issues reconciled; CI NFSv3 integration job green; matrix links the covered behavior and remaining gaps. |
| [M2: NFSv3 Protocol Conformance](https://github.com/GaTTGeng/NfsSharp/milestone/2) | Completed | RFC-grounded XDR, ONC RPC, portmapper, mount, and NFSv3 procedure semantics. | Focused fixtures cover valid, boundary, and malformed messages; primary NFSv3 flows run on NFS-Ganesha plus one additional server. |
| [M3: Reliability and Production I/O](https://github.com/GaTTGeng/NfsSharp/milestone/3) | Current | Deterministic recovery behavior under real transport and server failures. | Fault-injection suite covers timeout, cancellation, disconnect, restart, partial I/O, cache races, and stale handles; retry contract is documented. |
| [M4: RPCSEC_GSS and Kerberos](https://github.com/GaTTGeng/NfsSharp/milestone/4) | Planned | Interoperable AUTH_GSS authentication, integrity, and privacy. | Kerberos integration tests prove context creation, rollover, integrity, privacy, expiry, and failure cleanup on supported platforms. |
| [M5: NFSv4 Stabilization](https://github.com/GaTTGeng/NfsSharp/milestone/5) | Planned | A validated, versioned direct NFSv4 COMPOUND surface. | v4.0, v4.1, and v4.2 are validated independently for their supported operations, state/session lifecycle, recovery, and security; no facade promotion yet. |
| [M6: Public SDK Hardening](https://github.com/GaTTGeng/NfsSharp/milestone/6) | Cross-cutting | A consumable, diagnosable, package-quality SDK. | Public API review, XML docs, examples, diagnostics, nullable/analyzer checks, package validation, and support policy are complete for each released surface. |
| [M7: Compatibility Expansion Research](https://github.com/GaTTGeng/NfsSharp/milestone/7) | Research | Evidence-based expansion of support claims. | Published server/transport/framework matrix and an explicit decision for every proposed support tier. |

M6 runs alongside M3–M5 when a public surface changes. M7 informs M3–M5 but does not block a narrowly scoped release unless it changes an advertised compatibility claim.

## Delivered Scope and Suggested Next Work

### M1 — NFSv3 Integration Baseline (completed)

**In scope:** retain the Docker NFS-Ganesha fixture, deterministic test materialization, integration CI, test-result/server-log artifacts, and documented behavior evidence.

**Out of scope:** a second server, fault injection beyond the current reconnect coverage, and any new public capability.

**Tracker reconciliation:** [#14](https://github.com/GaTTGeng/NfsSharp/issues/14) directory enumeration, [#15](https://github.com/GaTTGeng/NfsSharp/issues/15) lookup/attributes/access/links, [#17](https://github.com/GaTTGeng/NfsSharp/issues/17) write/COMMIT, and [#19](https://github.com/GaTTGeng/NfsSharp/issues/19) attribute mutation were closed with matching `NfsV3IntegrationTests`, CI, and compatibility-matrix evidence.

### M2 — NFSv3 Protocol Conformance (completed)

Completed workstreams:

1. **XDR and record-marking limits ([#50](https://github.com/GaTTGeng/NfsSharp/issues/50), [PR #58](https://github.com/GaTTGeng/NfsSharp/pull/58)).** Added bounded framing and XDR fixtures for fragmentation, padding, maximum lengths, truncation, malformed values, and the 64 MiB RPC-record ceiling.
2. **ONC RPC reply and authentication semantics ([#51](https://github.com/GaTTGeng/NfsSharp/issues/51), [PR #59](https://github.com/GaTTGeng/NfsSharp/pull/59)).** Covered accepted/denied replies, verifier handling, XID behavior, malformed reply ordering, and AUTH_SYS boundary values.
3. **Portmapper and mount protocol variants ([#52](https://github.com/GaTTGeng/NfsSharp/issues/52), [PR #60](https://github.com/GaTTGeng/NfsSharp/pull/60)).** Covered unavailable mappings, wrong program/version/procedure replies, export-list variants, mount status mapping, and unmount failure behavior.
4. **Read, lookup, and directory result semantics ([#53](https://github.com/GaTTGeng/NfsSharp/issues/53), [PR #61](https://github.com/GaTTGeng/NfsSharp/pull/61)).** Added success, expected-status, optional-attribute, response-boundary, and directory-continuation fixtures.
5. **Mutation, durability, and capability result semantics ([#54](https://github.com/GaTTGeng/NfsSharp/issues/54), [PR #64](https://github.com/GaTTGeng/NfsSharp/pull/64)).** Added status-preserving mutation/WCC fixtures and WRITE, COMMIT, FSSTAT, FSINFO, and PATHCONF validation.
6. **Second-server interoperability baseline ([#55](https://github.com/GaTTGeng/NfsSharp/issues/55), [PR #65](https://github.com/GaTTGeng/NfsSharp/pull/65)).** Runs the primary workflow against Linux kernel NFS in addition to NFS-Ganesha, documents intentional differences, and retains only explicit server-independent or fixture-specific assertions in CI.

**Non-goals:** UDP support, automatic mutation replay, and adding new high-level APIs.

### M3 — Reliability and Production I/O

**Scope:** connection ownership and reuse, cancellation precedence, timeout disposal/reconnect, backoff policy, server restart and stale handles, partial reads/writes, COMMIT verifier changes, large-file transfer, directory-cache races, and concurrent callers.

**Suggested issues:** deterministic TCP fault proxy; cancellation/timeout race matrix; restart and stale-handle recovery; short/partial I/O and verifier-change handling; cache concurrency/invalidation policy; load and resource-limit characterization.

**Non-goals:** silently retrying unsafe mutations or promising exactly-once writes without a separately proven protocol contract.

### M4 — RPCSEC_GSS and Kerberos

**Scope:** mechanism negotiation, credential acquisition and refresh, RPCSEC_GSS control/data calls, integrity and privacy wrapping, context rollover/destruction, logging redaction, and cross-platform failure behavior.

**Suggested issues:** Kerberos test realm fixture; AUTH_GSS INIT/CONTINUE_INIT state machine; integrity mode; privacy mode; expiry/rekey/cleanup; negative authorization and malformed-token cases.

**Non-goals:** claiming Kerberos support from unit tests alone or exposing reusable credentials in logs/exceptions.

### M5 — NFSv4 Stabilization

Deliver by minor version, not as one broad NFSv4 claim:

1. **v4.0:** COMPOUND framing, path traversal, GETATTR/READDIR, OPEN/CLOSE/READ/WRITE, locking/delegation boundaries, and lease recovery.
2. **v4.1:** EXCHANGE_ID, CREATE_SESSION, SEQUENCE, slot management, session recovery, and backchannel/callback strategy.
3. **v4.2:** SEEK, ALLOCATE, DEALLOCATE, COPY, CLONE, and only the advanced operations whose state and error semantics have real-server evidence.

Each minor-version issue must name supported operations, server(s), auth mode, recovery expectations, and explicit unsupported operations. The existing direct `NfsV4Client` stays experimental until those gates are met.

### M6 — Public SDK Hardening (cross-cutting)

**Scope:** API review and versioning, XML documentation, runnable examples, structured and redacted diagnostics, nullable and analyzer hygiene, package/readme validation, release notes, support policy, and upgrade guidance.

**Release gate:** public changes have API documentation, a minimal example where useful, a compatibility-matrix update, package validation for net8.0/net9.0/net10.0, and no accidental experimental-to-supported promotion.

### M7 — Compatibility Expansion Research

**Scope:** server matrix (Linux kernel NFS, NFS-Ganesha FSAL variants, and additional server candidates), IPv6, TCP/UDP decision, target-framework policy, performance envelope, and support-tier cost.

**Output:** a published evidence table and an explicit decision: support in CI, document as known-compatible, keep experimental, or decline. Research work must not imply a product guarantee until its selected tier has automated evidence.

## Work Advancement and Evidence

A work item is ready to close when:

1. The intended behavior and failure semantics are captured by focused unit, protocol-fixture, integration, or fault-injection tests.
2. The test identifies its protocol version, authentication mode, server implementation, and any relevant transport assumptions.
3. The compatibility matrix records both verified behavior and known boundary conditions.
4. Public API changes include XML documentation and a usage example when appropriate.
5. Release builds, unit tests, and the applicable integration jobs pass for the repository-supported .NET targets.

Use the smallest test that proves the claim: unit tests for deterministic encoders/decoders; fixture tests for wire layout and malformed inputs; real-server tests for interoperability; fault injection for recovery; and server matrices only for intentional compatibility claims. Packet normalization may remove transport identifiers, but it must not hide observable procedure arguments, status codes, attributes, verifier changes, or ordering.

## Contributing

Start with an existing milestone issue where possible. For a newly discovered interoperability difference, [open an NFS compatibility issue](https://github.com/GaTTGeng/NfsSharp/issues/new?template=nfs_compatibility_gap.yml) with a minimal reproduction, NfsSharp version, protocol version, server implementation, expected behavior, actual behavior, and sanitized logs or packet evidence.
