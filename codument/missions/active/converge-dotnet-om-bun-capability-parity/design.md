# Design

## Desired State

The desired state is behavioral parity between `cozo-lib-dotnet` and `cozo-lib-bun` at the object/ontology-system boundary. “Parity” means equivalent domain effects, validation, temporal interpretation, authorization decisions, diagnostics, and inheritance behavior. It does not require identical language-level syntax.

The Bun implementation is a reference and evidence source, not an unquestionable specification. Current Codument behaviors, archived design intent, both test suites, and actual code paths are co-equal evidence. When they disagree, the mission follows the decision protocol below.

## Control Loop

| Actor | Role | Responsibility |
|---|---|---|
| MissionObserver | Sensor | Refresh G01-G14 against current source, tests, and behavior registries. |
| MissionReconciler | Controller | Compare the desired contracts with actual C# and Bun behavior; classify drift. |
| MissionPlanner | Desired-state processor | Create or revise tracks only after required decisions are resolved. |
| MissionApplier | Effect actor | Execute one bounded track, update evidence, and report actual verification. |
| User | Policy authority | Decide semantic tradeoffs that cannot be derived safely from evidence. |

After every completed top-level node, `MissionReconcile` checks whether evidence changed, a candidate track must be split, or a new decision is required. Replanning increments `Metadata.Revision` and records a report.

## Capability Slices

### Behavior Core

`sync-dotnet-om-behavior-runtime-parity` owns parent-action dispatch, inherited behavior tests, definition-time owner/scope validation, and atomic callback registration. Its handling of C# transactional action execution and typed delegates must follow user decision D4. Public validation facade APIs remain owned by G9's `complete-dotnet-om-public-surface-parity` track.

### Behavior Portability

`add-dotnet-om-behavior-portability` owns a serializable behavior catalog, callback-registration status, import diagnostics, and the user-approved JSON/YAML manifest. Metadata import must never pretend an absent executable callback is runnable.

### Optional Script Runtime

`add-dotnet-om-optional-script-runtime` owns an in-process Jint adapter in a package/capsule outside `Om.Core`, selected by D3=C and D4. It must define timeout, cancellation, resource limits, host allowlisting, error mapping, and callback binding.

### Permission Governance

`sync-dotnet-om-permission-governance-parity` owns graph-path witness evaluation, entity attributes, temporal reads, comparators, field hiding, and explanations. D1=A requires a hard cut to strict Bun-compatible, fail-closed evaluation without a legacy mode.

### Schema Evolution

`harden-dotnet-om-schema-evolution-parity` owns legacy relation upgrades, migration preflight, atomic apply/rollback, strictness, structured diagnostics, and fine-grained diff. D2=B requires additive strict V2 APIs, obsolete wrappers, and detect-only legacy initialization by default; D4 preserves C# snapshot breadth while adding structured comparison.

### Existential Governance

`sync-dotnet-om-existential-governance-parity` owns define-time operator validation, runtime alias re-resolution, and no-init-safe query behavior.

### Public Surface

`complete-dotnet-om-public-surface-parity` owns behaviorally missing APIs while preserving idiomatic C# shape under D0/D4. D5=C requires a typed `Keep | Set | Clear` parent patch and a migration path away from the ambiguous overload.

### Runtime Registry Lifecycle

`unify-om-runtime-registry-lifecycle` owns G15. The common lifecycle owner is one OM runtime instance. The internal runtime remains a DEPA data/dependency carrier; behavior operations stay in logic/processors. Bun gains an explicit runtime with an isolated callback registry, while legacy free functions delegate to one explicit legacy runtime adapter. .NET keeps one registry per `CozoOm` and gains public instance-local clear/reset. Clear/reset changes only runtime callback facts; persisted behavior definitions and binding identities remain intact.

### Bun Behavior Portability And Snapshot

`add-bun-om-behavior-portability` owns G16. Bun reuses the C# V1 canonical JSON wire contract for behavior kind, owner, name, callback slots, binding IDs, and readiness. The authoritative persisted facts are behavior definitions and `om_behavior_binding`; executable functions are runtime-local provider facts. Catalog readiness is a projection:

```text
ready = persisted binding identity exactly matches the current runtime registry binding
```

JavaScript source and function bodies are outside schema and manifest facts.

### Bun Behavior Schema Versioning

`extend-bun-om-behavior-schema-versioning` owns G17 after G16 establishes the persisted binding relation and catalog contract. Snapshot, keyed diff, migration, and rollback include all five behavior-definition relations plus `om_behavior_binding`. Restore never restores executable readiness. A legacy snapshot without a behavior section is rejected by default. The caller must explicitly choose `preserve` or `clear`; the default path has no persistent or runtime effects. New behavior-aware snapshots always carry an explicit behavior section, including an empty section.

## Decision Protocol

1. The observer records both implementations, tests, historical intent, and caller compatibility.
2. The reconciler labels the item `parity`, `preserve-csharp`, `preserve-bun`, or `human-decision`.
3. `parity` items may proceed directly.
4. `preserve-csharp` is only a recommendation until the user confirms it; evidence that Bun-like behavior would regress safety or maturity must be presented rather than treated as authorization.
5. `human-decision` produces a concise options document under `decisions/` and blocks affected tracks until the user answers.
6. The answer is written to `decisions.md`, the decision-tree frontier is closed, and downstream acceptance criteria are revised before implementation.

Resolved human-decision frontier:

- D0 observable semantic parity with idiomatic C# APIs;
- D1 hard cut to strict Bun permission semantics;
- D2 additive strict V2 migration/rollback APIs;
- D3 in-process Jint outside `Om.Core`;
- D4 preserve all identified mature C# mechanisms;
- D5 typed `Keep | Set | Clear` parent patch.

The decision frontier is closed: D3a selects canonical JSON with a YAML adapter, D3b selects inactive/unresolved metadata import with fail-closed execution and an atomic `requireReady` mode, and D8 selects fail-closed legacy behavior rollback with explicit `preserve | clear` override.

## Cross-Implementation Testing

Each implementation track must add or synchronize tests on both sides when the feature exists in both runtimes. Tests should share a scenario table where practical: setup ontology, command, expected effect/result, expected rejection, and temporal point. Language API-shape assertions remain local.

The final verification track must run both suites and publish a matrix with one row per G01-G14 item. A missing local runtime is a blocker to final completion, not evidence of parity.

The second verification track extends this matrix with:

- G15 runtime-instance registry isolation and clear/reset lifecycle;
- G16 cross-runtime behavior catalog/manifest/binding/readiness semantics;
- G17 Bun behavior-definition and binding snapshot/diff/rollback coverage.

## Risks

- Permission compatibility could turn a former allow into deny, or preserve an insecure allow.
- Migration and rollback changes can corrupt or partially rewrite schema state if transaction boundaries are wrong.
- Imported behavior metadata can create a false impression that callbacks are executable.
- In-process scripts can escape intended resource or trust boundaries.
- Mechanical copying can erase C# advantages such as transactions, typed contracts, and broader snapshots.

These risks are addressed through human gates, bounded tracks, paired tests, transaction probes, and an independent final verification track.
