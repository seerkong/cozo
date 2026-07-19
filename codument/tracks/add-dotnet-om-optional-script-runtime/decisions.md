# Decisions

## Resolved Mission Decisions

### Runtime and package boundary

Use in-process Jint in an optional package outside `Om.Core`. Preserve all mature C# transaction, typed API, readiness and DEPA package boundaries.

### Binding source

The canonical behavior manifest remains unchanged. Callers provide immutable script definitions programmatically, keyed by the manifest binding id. The provider emits `BehaviorCallbackBindingSet` and the existing import path performs state changes.

### Sandbox policy

Each invocation receives a fresh engine with cancellation, timeout, statement, memory and recursion limits. CLR access is disabled; no filesystem or network capability exists. Only the adapter's fixed behavior-kind-aware OM host façade is visible.

These controls are in-process limits, not OS/process isolation. The provider must not claim hostile-code sandboxing; if process isolation is required, it is a separate caller responsibility outside this track. Memory limits are required only to the extent supported by the pinned Jint version; unsupported memory-limit configuration fails before script execution.

### Compatibility

Native typed callbacks and manifests without script definitions keep their current behavior. Missing or incompatible scripts become structured provider diagnostics and participate in existing unresolved/`RequireReady` semantics. Same binding ids across native and script sources, or across incompatible script callback shapes, are deterministic merge/validation diagnostics before import effects.
