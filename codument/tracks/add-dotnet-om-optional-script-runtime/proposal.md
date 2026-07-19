# Change: Add Optional Jint Behavior Runtime

## Why

The Bun OM can express behavior callbacks directly in JavaScript. C# intentionally preserves typed delegates and a serializable metadata boundary, but currently has no optional way to supply those delegates from script. The approved mission decision selects in-process Jint while requiring that the engine stay outside `Om.Core` and operate within explicit safety limits.

## What Changes

- Add `cozo-lib-dotnet/packages/Om.Scripting.Jint` with a pinned Jint dependency; as of 2026-07-17 the selected NuGet version is `4.13.0`.
- Add immutable script definitions keyed by canonical manifest binding id and a provider that emits the existing typed callback binding set.
- Support constraint `when`/`then`/`validator`, computed, action, mutation and interceptor callback shapes.
- Project a behavior-kind-aware host façade for the existing OM context operations without exposing raw runtime/store or CLR objects.
- Enforce cancellation, timeout, statement and recursion limits with one fresh engine per invocation; apply Jint memory constraints when supported by the pinned version and otherwise report unsupported memory-limit configuration before execution.
- Map compile, execution, timeout, cancellation, host and result-conversion failures to stable adapter exceptions and diagnostics.
- Add executable tests proving ready/unresolved import binding, action inheritance/parent dispatch, transactional effects/rollback, sandbox denial and resource limits.

## Non-Goals

- Persisting or discovering script source in Cozo.
- Adding script source fields or a second script schema to the canonical behavior manifest.
- Filesystem/network modules, arbitrary CLR interop or arbitrary host-object injection.
- Replacing native typed callbacks or moving Jint into `Om.Core`.
- Multi-process isolation. D3 explicitly selected an in-process runtime.

## Compatibility

The change is additive. `Om.Core`, native callbacks and existing manifests remain source and behavior compatible. The adapter joins scripts to manifest callback slots by exact binding id plus the catalog slot's required typed delegate shape, so missing scripts naturally retain G4 unresolved behavior or fail `RequireReady` before effects.

## Impact

- New package: `cozo-lib-dotnet/packages/Om.Scripting.Jint/`.
- Tests: `cozo-lib-dotnet/tests/` and its project references.
- Core impact is limited to a genuinely necessary public provider-neutral contract adjustment, if implementation proves the existing `BehaviorCallbackBindingSet` insufficient; Jint types, script source serialization and script-specific schemas must never cross that boundary.
