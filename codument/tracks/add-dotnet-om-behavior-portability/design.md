# Design

## Facts And Truth Sources

The catalog is immutable serializable data. Behavior metadata remains in the five existing definition relations. A new additive relation, `om_behavior_binding`, persists callback identity using `(behavior_kind, owner_type, behavior_name, callback_slot, phase, seq) => binding_id`. Constraint `when` and `then` are separate slots; interceptor phase and exact sequence are part of the key.

Only binding identity is persistent. Executable delegates remain in the typed in-memory registry, whose immutable callback entries also carry the binding id used for that registration. `ready` is derived only when the persisted binding identity exactly matches the callback identity in one immutable registry snapshot; a delegate registered at the same behavior key under another or absent binding id remains unresolved. After process restart the same catalog exports its binding ids but honestly projects callbacks as unresolved until rebound. The new relation participates in schema initialization, snapshot/restore, and legacy additive initialization.

## Codec Boundary

JSON is the canonical versioned contract with deterministic entry ordering. Unknown versions, kinds, slots, malformed keys, duplicates, and conflicts fail during pure decode/preflight.

YAML lives in `cozo-lib-dotnet/packages/Om.Portability.Yaml`, pinned to YamlDotNet 18.1.0. The adapter parses a bounded YAML AST and converts only mapping/sequence/scalar nodes to the canonical catalog. It caps bytes, depth, and node count and rejects multiple documents, custom tags, merge keys, and unsafe alias use. It never deserializes arbitrary CLR object graphs. Om.Core has no YamlDotNet dependency and owns all normalization, validation, diagnostics, and import effects.

## Runtime Readiness Gates

Unresolved imported definitions are distinguishable from absent definitions. Readiness is checked at every actual behavior entry point:

- constraint evaluation, including both conditional callbacks and custom validators;
- computed property direct/entity-view and as-of reads;
- action dispatch and `CallParentActionAsync` owner advancement;
- nearest mutation resolution;
- before/after interceptor collection using exact `(owner, action, phase, seq)` definitions.

An unresolved definition never silently skips, falls back, or masquerades as undefined. The deterministic diagnostic identifies kind, owner, stable behavior key, callback slot, and binding id. Binding a compatible typed callback publishes a new registry snapshot and changes the derived readiness atomically.

## Staged Import And Atomic Visibility

Decode and conflict validation are effect-free. Import then captures the persistent pre-state and immutable registry state, resolves supplied typed bindings into a staged registry clone, and preflights all definitions. `requireReady=true` rejects before effects unless every callback slot is compatible; default mode allows unresolved metadata with diagnostics.

A runtime behavior gate coordinates import publication and behavior resolution. Import holds the gate while it applies persistent changes in a write transaction and atomically swaps the staged registry state. Behavior resolution captures a consistent registry snapshot under the gate, then releases it before invoking async user code. Concurrent readers therefore observe the complete pre-import or post-import state, never split metadata/registry readiness.

Persistent failure leaves the live registry unchanged. Registry publication failure after persistent commit restores the persistent snapshot while still holding the gate. Registry or persistence compensation failures are aggregated with the original failure. Interceptor staging preserves exact sparse sequences and allocates new sequences with `max(seq)+1`, never `list.Count`.

## API Shape

Use immutable records/options/results and async cancellation-aware facade methods. Typed binding sets are explicit per behavior kind. New public surface exposes catalog/import/export/readiness concepts, not mutable registry internals. Internal fault/barrier hooks may support deterministic atomicity tests without entering the product API.

## Boundaries

Om.Core does not evaluate scripts. G5 may later provide Jint-produced bindings through this contract. G9 owns validation facade APIs. Existing action transaction semantics remain unchanged.
