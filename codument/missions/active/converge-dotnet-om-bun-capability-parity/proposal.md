# Mission: converge-dotnet-om-bun-capability-parity

## Background

`cozo-lib-dotnet` originated from the Bun/Node.js OM design and has since developed a more explicit C# runtime, DEPA-style package boundaries, transactional action execution, typed inputs, and broader schema snapshots. The two implementations are therefore no longer related by a simple one-way port. The target is observable capability parity, not a line-by-line rewrite.

Two comparison waves identified 14 work items. The first wave covers behavior execution and portability:

1. `CallParentActionAsync`.
2. Inheritance tests for actions, mutations, and interceptors.
3. Explicit export/import of behavior definitions and callback registration state.
4. An optional JSON/YAML action manifest.
5. An optional script runtime outside the OM core.

The second wave covers permission path evaluation, full ABAC, strict rollback, legacy temporal upgrades, migration preflight/atomicity, structured schema diff, existential rule compatibility, behavior-definition guardrails, and lower-level public API gaps. The complete evidence matrix is in [evidence.md](evidence.md); the mission must keep that evidence current as implementation evolves.

A post-G01-G14 audit identified a third convergence wave:

1. Bun still owns executable callbacks in process-global maps while .NET owns them per `CozoOm` runtime.
2. Bun schema snapshots omit the five behavior-definition relations.
3. Bun has no persisted callback binding identity, portable behavior catalog, readiness projection, or canonical manifest compatible with the C# V1 contract.

The user selected runtime-instance registry ownership and the full long-term portability contract. Executable callback bodies and script source remain process-local and outside schema snapshots.

## Goal

Make the C# OM expose the capabilities users can rely on in the Bun/Node.js OM while retaining C# designs that are demonstrably more mature:

- align externally observable semantics and failure behavior;
- add corresponding Bun and C# test cases so the same feature contract is executable on both sides;
- present C# transactions, typed APIs, explicit registries, DEPA boundaries, and broad snapshot coverage as preservation candidates, then apply the user's decision;
- expose every intentional divergence in documentation and tests;
- stop for user decisions whenever neither side is an obvious winner, or when copying Bun would regress a more mature C# design.

## Non-Goals

- Do not replace the C# implementation with generated JavaScript bindings.
- Do not embed a JavaScript engine directly in `Om.Core`.
- Do not force identical method signatures where idiomatic C# and JavaScript APIs can express the same behavior differently.
- Do not reopen type-system parity items already completed by `sync-dotnet-om-type-system-parity` unless fresh evidence proves regression.
- Do not redesign analytics, batch, Wiki, Java/Spring indexing, or higher application ontology layers.
- Do not silently choose compatibility, security, migration, or runtime policies at mission execution time.

## Decision Covenant

The mission uses `QuestionSeverity=deep`. D0-D5 and D3a/D3b have been answered and frozen in [decisions.md](decisions.md). Fresh contradictory evidence must reopen the affected decision before implementation diverges from it.

The executor must ask the user before selecting a side when:

- both implementations have meaningful advantages and preserving one changes observable semantics;
- C# is clearly more mature and matching Bun literally would remove safety, type information, transactionality, or architecture boundaries;
- a compatibility overload, default value, serialization schema, sandbox boundary, or security failure mode must be selected.

Even when evidence clearly suggests that C# is more mature, the executor must still present the evidence and proposed preservation strategy to the user before implementation. No “temporary” implementation may bypass a pending decision.

## Success Criteria

- Every evidence item G01-G14 maps to a bound track, a passing parity test, or an explicit user-approved intentional difference.
- Both implementations contain corresponding test cases for the shared feature contract; language-specific API-shape tests may differ.
- Permission checks evaluate required graph paths and support entity-aware, temporal ABAC with field-level decisions and explanations.
- Schema migration and rollback provide the agreed strictness, transactionality, diagnostics, legacy upgrade behavior, and structured diff.
- Behavior inheritance supports parent-action dispatch and inherited interceptors, with definition-time guardrails and portable metadata/callback status.
- Existential rules remain valid across schema aliases and reject unsupported predicates before persistence.
- The agreed manifest and optional script runtime are isolated from `Om.Core` and have explicit safety boundaries.
- Public APIs cover the agreed traversal, validation, parent-clearing, and value-type inspection capabilities.
- Bun and .NET expose instance-local registry clear/reset semantics; clearing one runtime does not affect another runtime or delete persisted behavior facts.
- Bun uses the C# V1 canonical JSON behavior manifest contract, persists callback binding identity, derives `unbound | unresolved | ready` from persisted and runtime facts, and fails closed for unresolved execution.
- Bun schema snapshot, keyed diff, migration, and rollback cover constraint, computed, action, mutation, interceptor, and behavior-binding relations without serializing executable source; legacy snapshots with no behavior section follow an explicit user-approved policy.
- `dotnet build`, C# OM tests, Bun OM tests, Codument strict validation, and a final G01-G14 evidence audit pass.
- The final verification extends the evidence matrix with G15-G17 for registry lifecycle, behavior portability, and Bun behavior snapshot coverage.

## Why This Is A Mission

This work crosses runtime behavior, persistence, security, schema evolution, serialization, optional runtime integration, public APIs, and two test suites. It needs multiple independently reviewable tracks plus explicit human decisions and a final cross-implementation verification track. `mission.xml` owns the control loop; each code/spec/test change remains owned by a real track.
