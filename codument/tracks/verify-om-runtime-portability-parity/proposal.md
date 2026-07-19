# Change: Verify OM Runtime Portability Parity

## Context And Why

The first mission verification closed G01-G14. A later evidence refresh added
G15-G17: runtime-instance registry lifecycle, Bun's C# V1-compatible behavior
portability contract, and Bun behavior-aware schema versioning. Their linked
implementation tracks are complete, but the active mission needs one fresh,
independent closure matrix before it can satisfy its completed gate.

## Goals / Non-Goals

**Goals:**

- Independently verify G15, G16, and G17 through source evidence and executable
  Bun/.NET scenarios.
- Re-run appropriate full and focused gates, strict-validate all linked tracks,
  and publish an issues-first G15-G17 matrix.
- Confirm that only durable behavior metadata and binding identity cross the
  persistence boundary, while executable callback/provider facts remain local.
- Re-run the active mission completed gate with traceable evidence.

**Non-Goals:**

- Redesign D6-D8 or alter their resolved semantics.
- Serialize JavaScript function bodies, script source, or runtime callback
  state into a schema snapshot or behavior manifest.
- Refactor unrelated dirty worktree changes.

## What Changes

- Add a verification-only Codument track for the G15-G17 evidence matrix.
- Add focused paired tests only if the audit identifies an untested, already
  required observable contract.
- Record verification, attractor, GapLoop, and archive-readiness reports.

## Impact

- Affected behavior: `runtime-portability-verification`.
- Affected code: Bun OM, .NET OM, their tests, and active mission artifacts
  only if a real verification gap is found.
