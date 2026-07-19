# Design

## Fact Boundaries

```text
Cozo relations:
  behavior definitions + callback binding identity

Runtime registry:
  JavaScript callback + optional binding identity

Derived projection:
  catalog + readiness + canonical manifest
```

`ready` is never imported as truth. It is derived using ordinal identity:

```text
no persisted binding                         -> unbound
persisted binding, no exact runtime binding  -> unresolved
persisted binding == runtime binding         -> ready
```

## Persistent Binding Key

`om_behavior_binding` uses:

```text
behavior_kind, owner_type, behavior_name,
callback_slot, phase, seq => binding_id
```

Non-interceptors use `phase=""` and `seq=-1`. Interceptors require `before|after` and non-negative `seq`.

## Runtime Registry

Each registration stores callback and optional binding ID in the runtime snapshot. Add explicit `registerConstraint`, `registerValidator`, `registerComputed`, `registerAction`, `registerMutation`, and `registerInterceptor` APIs that do not write schema facts.

Existing `define*` APIs remain combined metadata-plus-unbound-callback compatibility APIs. `custom` constraints use one validator callback returning `string|null`; other constraint types use independent `when` and `then` callbacks.

`addInterceptor` allocates a sequence above both persisted metadata and current runtime entries, so clear/restart and sparse imported sequences cannot overwrite definitions.

## Canonical Manifest V1

The JSON root is `{version:1, behaviors:[...]}`. Property order, wire enum names, null fields, behavior order, callback order, escaping, and UTF-8 bytes match `.NET` `BehaviorManifestJsonCodec`.

Decode reports `OMM1000-OMM1302` diagnostics. Import reports `OMI*`; unresolved execution reports `OMR1001` with kind, owner, behavior key, slot, binding ID, phase and sequence.

## Execution

Every behavior command captures one immutable runtime registry snapshot. Persistent definitions and binding rows are resolved before callback execution.

- Bound mismatch or absence fails closed.
- An unresolved child action does not fall back to a parent action.
- Direct and action-returned mutation batches resolve before the first callback.
- Before and after interceptors resolve before the action begins.
- Computed readiness is checked for current, as-of and entity-view reads.
- Constraint readiness applies to explicit validation and property/edge write paths.

Legacy unbound callbacks remain executable.

## Import

Import performs:

1. Pure decode and canonical validation.
2. Callback binding index validation and owner/constraint-shape preflight.
3. Runtime gate acquisition and registry snapshot capture.
4. Staging callbacks and deriving unresolved diagnostics.
5. `requireReady` zero-effect rejection when needed.
6. One write transaction for definitions and binding rows.
7. CAS-style runtime snapshot publication.
8. Persistent compensation if publication fails.

Callbacks execute after releasing the import gate.

## Concurrency

The consistency boundary is one runtime instance. A database transaction makes persistent import atomic. Two runtimes sharing a database share definition/binding facts but retain independent callback snapshots and readiness.

## DEPA

- Definitions and binding identities are schema Data.
- Runtime callbacks are process-local Data.
- Catalog, readiness, decode and validation are Processors.
- Register, clear, import and execute are Effects.
- The runtime carrier owns lifecycle state but does not contain domain behavior logic.
