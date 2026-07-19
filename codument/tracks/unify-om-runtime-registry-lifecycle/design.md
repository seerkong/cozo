# Design

## Common Contract

Registry ownership is one OM runtime instance. Runtime callback state and persisted behavior facts are separate data domains.

```text
runtime = { store/runner, registry, gate/options }
clear(runtime) -> empty runtime callback snapshot
persisted behavior definitions/bindings -> unchanged
```

## Bun

`createOmRuntime(runner)` returns a branded data carrier. Core helpers unwrap its runner for Cozo effects and select its registry for behavior resolution. Existing APIs continue accepting a plain runner; those calls resolve through one explicit legacy runtime/registry adapter.

Behavior execution captures one registry snapshot at command entry and uses that resolution scope for action inheritance, parent calls, interceptors, mutations, constraints, and computed callbacks. Registry writes and clear publish a new snapshot instead of mutating a snapshot already used by an in-flight command.

`clearRegistry(runtime)` clears only the supplied runtime. `clearRegistry()` clears only the compatibility runtime.

## .NET

`CozoOm` remains the runtime owner. Add `ClearRegistryAsync(CancellationToken)` to the facade. It enters `BehaviorRuntimeGate`, then atomically publishes `CozoOmRegistrySnapshot.Empty`. The operation does not touch `om_*` relations.

## DEPA Boundaries

- Runtime is a dependency/state carrier, not a business-logic container.
- Registry publication and clear are explicit effects.
- Behavior resolution is a processor over one immutable registry snapshot.
- Legacy Bun functions are adapters, not a second source of truth.
- Persisted schema and runtime executable state remain independent facts.

## Compatibility

- Plain Bun runner calls preserve current behavior through the legacy adapter.
- Runtime-aware APIs are additive.
- No persisted schema migration occurs in this track.
- C# API remains idiomatic async because clear is serialized through an async gate.
