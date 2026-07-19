# Runtime Registry And Bun Portability Decisions

## Status

Resolved by the user on 2026-07-18.

## D6 Runtime Registry Lifecycle

The common lifecycle owner is one OM runtime instance.

- Bun introduces an explicit runtime-instance callback registry.
- .NET preserves the existing `CozoOm`-instance registry.
- Both expose instance-local clear/reset semantics.
- Bun legacy free functions are compatibility adapters over one explicit legacy runtime.
- Clearing callbacks does not delete persisted behavior definitions or binding identities.

## D7 Bun Callback Binding Portability

The long-term full portability contract is selected.

- Bun adopts the C# V1 canonical JSON behavior catalog and manifest wire format.
- Binding identity is persisted and included in schema snapshots.
- Readiness is derived from persisted binding identity plus the current runtime registry.
- Missing runtime callbacks remain unresolved and execution fails closed.
- Strict import may require all callbacks to be ready atomically.
- JavaScript function bodies and script source are never persisted as schema facts.

## D8 Legacy Snapshot Rollback

Resolved by the user on 2026-07-18 with option C.

- A legacy snapshot without a behavior section is incompatible by default.
- Rollback fails closed with a structured diagnostic and no effects.
- An explicit `preserve` policy keeps current behavior definitions and binding identities.
- An explicit `clear` policy removes behavior definitions and binding identities.
- New behavior-aware snapshots always include an explicit behavior section, even when empty.
- Runtime callbacks are never restored; readiness is re-derived from persistent bindings and the current runtime registry.

## DEPA Boundary

- Persisted definitions and binding identities are schema Data.
- Runtime callback registrations are process-local runtime Data.
- Readiness and manifests are derived Processor outputs.
- Register, clear, import, restore, and execute are explicit Effects.
- The runtime instance is the single owner of mutable callback state and remains a dependency/state carrier rather than a business-logic container.
- Missing historical behavior data remains unknown; only an explicit policy may turn that unknown into a preserve or destructive clear effect.
