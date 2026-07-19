# Proposal: Sync .NET OM Behavior Runtime Parity

## Problem

The C# OM behavior runtime implements transactional action execution and nearest-owner action/mutation lookup, but it does not yet expose Bun's parent-action dispatch and does not preserve Bun's inherited interceptor ordering in all cases. Behavior definition paths also allow invalid owner/scope/phase inputs or callback registration failures to leave metadata and in-memory registry state out of sync.

Mission G3 explicitly groups G01, G02, and the behavior-definition portion of G13 into this track. Public facade work remains in G9.

## Goals

- Add `OmActionContext.CallParentActionAsync` with nearest-parent-owner lookup and stable `InvalidOperationException` diagnostics that identify the requested action and current owner.
- Keep parent action execution inside the existing C# transaction and return mutation specifications for child composition, matching Bun semantics.
- Preserve ancestor-to-child interceptor grouping and each owner's registration order.
- Validate behavior owner type, constraint scope, interceptor phase, and callback arguments before metadata or registry state changes.
- Prevent failed behavior definitions from leaving ghost callbacks or orphaned metadata in either failure direction.
- Add corresponding C# and Bun tests for inherited actions, mutations, interceptors, overrides, failures, and rollback.

## Non-Goals

- Do not add manifest import/export or callback readiness catalogs; G4 owns portability.
- Do not add Jint or any script runtime; G5 owns scripting.
- Do not add public `ValidateConstraints`, traversal, or value-type inference facades; G9 owns public surface parity.
- Do not change permission, schema migration, existential rule, or type-parent update semantics.
- Do not replace C# transactional execution with Bun's implementation structure.

## Proposed Changes

### Parent Action Dispatch

Add an action-context method that starts above `ActionOwnerType`, resolves the nearest ancestor registration for the requested action, creates a context whose owner is that registration, and invokes only the parent handler. The returned mutation list is composed by the overriding handler and executed once by the existing outer action transaction.

### Stable Inherited Interceptors

Collect interceptor registrations by owner chain from the root ancestor to the concrete type. Preserve each owner's `Seq` order without globally sorting owner-local sequence numbers across the flattened chain.

### Definition Guardrails

Before persistent or registry changes:

- require an existing owner type for constraints, computed properties, actions, mutations, and interceptors;
- reject unsupported constraint scopes;
- reject interceptor phases other than `before` or `after`;
- reject null callbacks;
- coordinate persistent metadata and callback registration so either failure direction is compensated: storage failure leaves no callback, and callback registration failure removes newly written metadata.

## Compatibility

The change is additive for action contexts and corrective for invalid definitions. Existing valid C# behavior registrations keep their API and transactional semantics. Existing unknown constraint scope strings become rejected, as required by the mission's behavior-definition guardrail.

## Acceptance

- C# tests cover direct and multi-level inheritance, child override with parent composition, chain-end errors, inherited mutation lookup, exact interceptor order, and rollback.
- Invalid owner/scope/phase/callback cases leave neither persistent definitions nor runtime registrations.
- Bun reference tests cover the corresponding observable contracts.
- Available C# and Bun test commands pass; missing runtimes are recorded explicitly rather than treated as success.
