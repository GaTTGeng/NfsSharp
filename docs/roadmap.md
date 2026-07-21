# NfsSharp Roadmap

NfsSharp is evolving into a dependable managed NFS SDK for .NET applications that need direct protocol access without invoking native NFS tools at runtime. NFSv3 over TCP is the supported product path; the direct NFSv4 APIs remain experimental.

> This document records delivery intent and acceptance criteria, not release dates or a compatibility guarantee. GitHub milestones and issues are the source of truth for work assignment. The [compatibility matrix](nfs-compatibility.md) is the source of truth for supported behavior.

## Current Focus: Close M1, Then Start M2

The repository already has a repeatable NFS-Ganesha NFSv3 integration job, CI artifact collection, and real-server tests for discovery, mount lifecycle, metadata, directory traversal, I/O, mutations, capability queries, cache behavior, cancellation, and a reconnect/retry path. This is enough evidence to reconcile and close **M1: NFSv3 Integration Baseline** once its issue tracker matches that evidence.

The next implementation milestone is **M2: NFSv3 Protocol Conformance**. It turns the existing behavior coverage into procedure-level protocol confidence: reproducible wire fixtures, malformed input limits, portmapper and mount variants, and representative-server checks. M3 reliability work may begin only where it supplies a deterministic fault harness needed by M2; it must not turn into an unbounded production-features stream before M2 has clear procedure semantics.

### Immediate tracker actions

1. Reconcile M1 issues [#14](https://github.com/GaTTGeng/NfsSharp/issues/14), [#15](https://github.com/GaTTGeng/NfsSharp/issues/15), [#17](https://github.com/GaTTGeng/NfsSharp/issues/17), and [#19](https://github.com/GaTTGeng/NfsSharp/issues/19) against their current integration tests and compatibility-matrix rows. Close each issue only after its acceptance evidence is linked from the issue.
2. Close [M1](https://github.com/GaTTGeng/NfsSharp/milestone/1) after those four issues are reconciled and the NFSv3 integration CI job is green on `master`.
3. Create the M2 issues listed below, label them `protocol` and `compatibility`, and give each a focused RFC section, test fixture, and server-evidence requirement.

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
| [M1: NFSv3 Integration Baseline](https://github.com/GaTTGeng/NfsSharp/milestone/1) | Closing | Reproducible NFSv3 real-server baseline and evidence trail. | All M1 issues reconciled; CI NFSv3 integration job green; matrix links the covered behavior and remaining gaps. |
| [M2: NFSv3 Protocol Conformance](https://github.com/GaTTGeng/NfsSharp/milestone/2) | Next | RFC-grounded XDR, ONC RPC, portmapper, mount, and NFSv3 procedure semantics. | Focused fixtures cover valid, boundary, and malformed messages; primary NFSv3 flows run on NFS-Ganesha plus one additional server. |
| [M3: Reliability and Production I/O](https://github.com/GaTTGeng/NfsSharp/milestone/3) | Planned | Deterministic recovery behavior under real transport and server failures. | Fault-injection suite covers timeout, cancellation, disconnect, restart, partial I/O, cache races, and stale handles; retry contract is documented. |
| [M4: RPCSEC_GSS and Kerberos](https://github.com/GaTTGeng/NfsSharp/milestone/4) | Planned | Interoperable AUTH_GSS authentication, integrity, and privacy. | Kerberos integration tests prove context creation, rollover, integrity, privacy, expiry, and failure cleanup on supported platforms. |
| [M5: NFSv4 Stabilization](https://github.com/GaTTGeng/NfsSharp/milestone/5) | Planned | A validated, versioned direct NFSv4 COMPOUND surface. | v4.0, v4.1, and v4.2 are validated independently for their supported operations, state/session lifecycle, recovery, and security; no facade promotion yet. |
| [M6: Public SDK Hardening](https://github.com/GaTTGeng/NfsSharp/milestone/6) | Cross-cutting | A consumable, diagnosable, package-quality SDK. | Public API review, XML docs, examples, diagnostics, nullable/analyzer checks, package validation, and support policy are complete for each released surface. |
| [M7: Compatibility Expansion Research](https://github.com/GaTTGeng/NfsSharp/milestone/7) | Research | Evidence-based expansion of support claims. | Published server/transport/framework matrix and an explicit decision for every proposed support tier. |

M6 runs alongside M2–M5 when a public surface changes. M7 informs M2–M5 but does not block a narrowly scoped release unless it changes an advertised compatibility claim.

## Scope and Suggested Issue Breakdown

### M1 — NFSv3 Integration Baseline (closing)

**In scope:** retain the Docker NFS-Ganesha fixture, deterministic test materialization, integration CI, test-result/server-log artifacts, and documented behavior evidence.

**Out of scope:** a second server, fault injection beyond the current reconnect coverage, and any new public capability.

**Tracker reconciliation:** #14 directory enumeration, #15 lookup/attributes/access/links, #17 write/COMMIT, and #19 attribute mutation already have matching coverage in `NfsV3IntegrationTests` and matrix rows. Their closing comments should name the test method(s), CI workflow, and any remaining cross-server gaps rather than silently implying universal support.

### M2 — NFSv3 Protocol Conformance (next)

Create focused issues in this order:

1. **XDR and record-marking limits.** Add fixtures for fragmentation, padding, maximum lengths, truncation, invalid booleans/enums, and the 64 MiB RPC-record ceiling.
2. **ONC RPC reply and authentication semantics.** Cover accepted/denied replies, verifier handling, XID behavior, malformed reply ordering, and AUTH_SYS boundary values.
3. **Portmapper and mount protocol variants.** Cover unavailable mappings, wrong program/version/procedure, empty and denied exports, mount status mapping, and unmount failure behavior.
4. **NFSv3 procedure result semantics.** For every supported procedure, test success, expected NFS status, missing optional attributes, count/EOF consistency, cookie-verifier behavior, and response-size boundaries.
5. **Second-server interoperability baseline.** Run the existing primary workflow against one maintained server distinct from the repository's in-memory NFS-Ganesha fixture (for example Linux kernel NFS), document intentional differences, and add only stable assertions to CI.

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
