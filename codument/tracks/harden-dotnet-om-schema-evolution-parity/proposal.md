# Change: Harden .NET OM Schema Evolution Parity

## Context And Why

The existing C# schema migration and rollback APIs do not provide Bun's strict
preflight, transactional application, diagnostic results or legacy-upgrade
safety. Their coarse diff also hides definition-level change information.

## Goals / Non-Goals

**Goals:**
- Add atomic strict-by-default V2 migration and rollback APIs.
- Add effect-free preflight, structured diagnostics/results and keyed diffs.
- Detect legacy relation shapes by default and provide explicit idempotent
  upgrade paths.
- Keep compatibility APIs during transition and preserve broad C# snapshots.

**Non-Goals:**
- Remove existing migration APIs in this track.
- Silently upgrade legacy user data by default.
- Replace C# schema snapshots with Bun's narrower serialization.

## Impact

`Om.Core` contracts, facade, schema logic and focused C#/Bun parity tests will
change. Existing callers continue to compile through obsolete wrappers.
