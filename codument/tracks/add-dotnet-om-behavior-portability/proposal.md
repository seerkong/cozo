# Proposal

## Problem

C# OM persists behavior metadata and keeps executable delegates in a typed in-memory registry, but it cannot export/import the combined definition state or explain which imported behaviors are executable. Copying metadata alone can create a false-ready runtime.

## Goal

Add a versioned portable behavior catalog, canonical JSON manifest, YAML adapter, explicit callback readiness, fail-closed execution, and atomic `requireReady` import while preserving the existing C# transaction and typed-registry design.

## In Scope

- Constraint, computed, action, mutation, and interceptor metadata.
- Stable callback binding identifiers and ready/unresolved diagnostics.
- Deterministic versioned JSON plus a semantically equivalent YAML adapter.
- Additive persisted callback binding identities with readiness derived from the current typed-registry snapshot.
- Permissive unresolved import and strict atomic `requireReady` import.
- Round-trip, restart, concurrency, conflict, version, fail-closed, and rollback/compensation tests.

## Out Of Scope

- JavaScript/Jint execution, script trust/resource policy, or source evaluation (G5).
- Public validation facade and unrelated public API parity (G9).
- Permission, schema migration, or existential-rule governance (G6-G8).

## Success Criteria

Equivalent JSON/YAML manifests produce the same catalog and diagnostics; binding identities survive restart while readiness stays honest; missing callbacks never execute through any behavior entry point; concurrent strict import exposes no split state; complete C# OM tests and final GapLoop pass.
