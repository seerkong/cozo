# Design: Optional Jint Behavior Runtime

## Architecture

`Om.Scripting.Jint` is an adapter that consumes three values: a decoded `BehaviorCatalog`, immutable script definitions keyed by binding id, and strict runtime options. It validates the exact callback-slot shape and returns a `BehaviorCallbackBindingSet` plus structured diagnostics. Callers pass that set into the existing canonical import facade; the adapter does not persist metadata or publish registry state itself.

The package pins Jint `4.13.0`, the current NuGet gallery version verified on 2026-07-17. The root `Cozo.DotNet.csproj` and all `src/Om.Core` files remain free of Jint references.

## Script Contract

Each source evaluates to one callable function. Its callback shape is selected by the catalog slot that references the binding id. The adapter reads the current G4 `BehaviorCatalogEntry.Callbacks` entries and maps each non-null `BehaviorCallbackBinding.BindingId` by exact ordinal string equality to the slot's required delegate:

- `BehaviorCatalogKind.Constraint` + `When` or `Then` -> `BehaviorConstraintCallbackBinding`, boolean result;
- `BehaviorCatalogKind.Constraint` + `Validator` -> `BehaviorValidatorCallbackBinding`, null for success or a string error;
- `BehaviorCatalogKind.Computed` + `Compute` -> `BehaviorComputedCallbackBinding`, JSON-compatible value;
- `BehaviorCatalogKind.Action` + `Handler` -> `BehaviorActionCallbackBinding`, ordered `MutationSpec` objects;
- `BehaviorCatalogKind.Mutation` + `Executor` -> `BehaviorMutationCallbackBinding`, no required result;
- `BehaviorCatalogKind.Interceptor` + `Handler` -> `BehaviorInterceptorCallbackBinding`, no required result.

The provider rejects one binding id being referenced by incompatible callback shapes before returning any bindings. If a binding id is referenced by multiple compatible slots, the same compiled source may be reused only when doing so keeps the slot identity in diagnostics and does not require duplicate entries in a single `BehaviorCallbackBindingSet` typed collection. Value conversion uses JSON-compatible data and explicit adapters; raw CLR runtime objects are not passed into JavaScript.

There is no script manifest and no script-source field in the canonical manifest. Script definitions are programmatic, immutable inputs to the provider. The canonical JSON/YAML behavior manifest remains the only serialized metadata schema, and binding id remains only a join key between metadata and executable callback providers.

## Host Capability Boundary

Scripts receive immutable identity/parameter data and a fixed host façade. Capabilities are projected only where the corresponding C# context supports them:

- property reads and temporal reads;
- neighbor reads;
- property writes and relation links for mutation/action/interceptor contexts;
- parent-action dispatch for action contexts.

Unknown members are absent. The adapter never calls `AllowClr`, exposes `CozoOmRuntime`, accepts arbitrary host objects, installs module loaders or provides filesystem/network APIs. Async host operations are exposed as promise-returning host methods and are awaited by the adapter under the invocation cancellation token. The bridge must not block on `.Result` or `.Wait()`, and cancellation must prevent a late host continuation from publishing effects after the invocation has failed or timed out.

## Isolation and Limits

Every callback invocation creates a fresh engine from immutable options. Defaults are finite and callers may only tighten or explicitly configure finite positive values for:

- wall-clock timeout;
- cancellation token;
- maximum statements;
- memory limit where supported by the pinned Jint version;
- recursion depth.

No engine or mutable JavaScript global state is shared across invocations. Timeout/cancellation must interrupt both script execution and adapter-mediated async host work as far as the underlying operation supports cancellation. These controls are in-process defense-in-depth limits, not an OS/process sandbox; hostile untrusted code remains out of scope unless the caller supplies separate process isolation.

## Error Model

`JintBehaviorScriptException` carries a stable code, binding id, callback slot, failure phase and optional source location. Phases distinguish validation/compile, execution, timeout, cancellation, host invocation and result conversion. The original exception is retained but messages do not embed full source or host secrets. Provider construction failures are returned as deterministic diagnostics before import effects.

## Transaction and Readiness Integration

Generated delegates execute through the existing action/mutation/interceptor pipeline, so transactional commit and rollback remain controlled by `Om.Core`. Direct write/link host calls execute through the callback context and therefore participate in the same transaction as native callbacks. Action scripts return ordered `MutationSpec` objects; `host.callParentAction(...)` returns the parent action's mutation specs and does not auto-apply them, so the child script must explicitly return or compose them for the existing action pipeline to apply. The provider never bypasses the behavior gate or registry. Exact script binding ids are attached to generated callbacks, allowing existing readiness projection, restart behavior and `RequireReady` import to work unchanged.

Native callbacks and script callbacks coexist by constructing a single provider-neutral `BehaviorCallbackBindingSet` for import. A script-generated binding must not overwrite a native binding with the same id and delegate shape silently; merge conflicts are deterministic diagnostics before import effects.

## Test Strategy

- Package/dependency isolation and immutable options/source definitions.
- Every callback kind, exact binding identity, exact `BehaviorCatalogCallbackSlot` mapping and canonical manifest import.
- Missing, duplicate and incompatible scripts with no import effects, including incompatible reuse of one binding id across action/interceptor or other distinct delegate shapes.
- Property/neighbor reads, mutation effects, parent action and inherited interceptors through the allowlisted façade.
- Timeout, cancellation, statement, recursion and infinite-loop probes; memory-limit tests must either prove the configured Jint memory limit works for the pinned version or prove unsupported memory-limit configuration is rejected before script execution.
- Denial of CLR, filesystem, network, module loading and unlisted host members.
- Stable compile/runtime/conversion/host errors without source leakage.
- Fresh-engine isolation, concurrent invocations, cancellation during async host work and transactional rollback after script/host failure.
- Native/script coexistence: native typed callbacks and script-generated callbacks import together, and same-id merge conflicts are diagnosed before effects.

## Risks

- JavaScript resource limits are defense in depth inside the process, not an OS sandbox. The API and docs must state that callers should not execute hostile untrusted code when process isolation is required.
- Async promise/host bridging can accidentally deadlock or outlive cancellation. Tests must exercise cancellation during host work, not only pure infinite loops.
- Over-broad value conversion can expose CLR objects. All crossings use explicit JSON-shaped projections.
