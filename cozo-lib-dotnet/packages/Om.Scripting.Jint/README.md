# Cozo .NET OM Jint Behavior Adapter

`Cozo.DotNet.Om.Scripting.Jint` is an optional adapter that resolves canonical
OM behavior binding IDs to JavaScript callbacks. It uses Jint `4.13.0`, pinned
by this package. The root `Cozo.DotNet` project and `Om.Core` do not depend on
Jint and do not contain a script serialization schema.

The adapter is deliberately narrow:

- Canonical `BehaviorCatalog` JSON is the persisted behavior metadata.
- Script source and `sourceName` are programmatic inputs keyed by an exact,
  ordinal `bindingId`.
- `BuildBindings` produces the existing provider-neutral
  `BehaviorCallbackBindingSet`.
- The existing manifest import API owns readiness, registry publication, and
  transaction behavior.

Script source and `sourceName` are never serialized into or persisted with the
canonical behavior manifest.

## Minimal Binding Flow

```csharp
using Cozo.DotNet.Om.Contracts.Models;
using Cozo.DotNet.Om.Scripting.Jint;

var decoded = BehaviorManifestJsonCodec.Decode(canonicalManifestJson);
if (!decoded.Success || decoded.Catalog is null)
{
    throw new InvalidOperationException("Invalid canonical behavior manifest.");
}

var definitions = new[]
{
    new JintBehaviorScriptDefinition(
        "asset:action:retire",
        """
        (identity, params) => [
          { mutation: "set_status", params: { status: params.status } }
        ]
        """,
        "retire-asset.js"),
};

var provider = JintBehaviorScriptProvider.BuildBindings(
    new JintBehaviorScriptBindingRequest(decoded.Catalog, definitions));
if (!provider.Success)
{
    throw new InvalidOperationException(
        string.Join(Environment.NewLine, provider.Diagnostics));
}

var import = await om.ImportBehaviorManifestJsonAsync(
    canonicalManifestJson,
    provider.Bindings,
    new BehaviorImportOptions(RequireReady: true));
```

The catalog and script definitions are copied into immutable collections. A
binding ID match is case-sensitive and ordinal. Keep source loading, secret
handling, and deployment policy outside this package.

## JavaScript Callback Contract

Each source expression must evaluate to one callable function. Functions may
be synchronous or return a Promise.

| Behavior slot | JavaScript signature | Required result |
| --- | --- | --- |
| Constraint `when` | `(identity, host) => boolean` | Boolean |
| Constraint `then` | `(identity, host) => boolean` | Boolean |
| Constraint `validator` | `(identity, host) => null \| string` | `null` for success or an error string |
| Computed `compute` | `(identity, host) => JsonValue` | JSON-compatible value |
| Action `handler` | `(identity, params, host) => MutationSpec[]` | Ordered mutation-spec array |
| Mutation `executor` | `(identity, params, host) => void` | Result ignored |
| Interceptor `handler` | `(identity, host) => void` | Result ignored |

`JsonValue` means `null`, a string, a boolean, a finite number, an array of
JSON values, or a plain object with string keys and JSON values. Values such as
functions, `Date`, `Map`, cyclic objects, non-finite numbers, and CLR objects
are rejected.

An action result uses this JSON shape:

```javascript
[
  { mutation: "set_status", params: { status: "retired" } },
  { mutation: "write_audit" }
]
```

`mutation` must be a non-blank string. `params` is optional and, when present,
must be a JSON object.

### Identity And Parameters

`identity` and `params` are JSON projections that are recursively frozen before
the callback starts. They do not expose raw CLR context objects.

- Validation: `{ entityId, typeName }`
- Computed: `{ entityId, typeName, asOf }`
- Mutation: `{ entityId, typeName }`
- Action/interceptor: `{ entityId, typeName, actionOwnerType, parameters }`

Action and mutation callbacks also receive their invocation parameters as the
separate `params` argument. Interceptors receive action parameters through
`identity.parameters`. Treat all input values as immutable.

## Allowlisted OM Host

The frozen `host` object contains only methods allowed for the current behavior
kind. Every method is asynchronous and returns a Promise.

| Method | Constraint / validator | Computed | Mutation | Action | Interceptor |
| --- | --- | --- | --- | --- | --- |
| `getProperty(attrName)` | Yes | Yes | Yes | Yes | Yes |
| `getPropertyAsOf(attrName, asOf)` | Yes | No | Yes | Yes | Yes |
| `getNeighbors(relName?, direction?)` | Yes | Yes | Yes | Yes | Yes |
| `setProperty(attrName, value, options?)` | No | No | Yes | Yes | Yes |
| `linkEntities(relName, toId, props?, options?)` | No | No | Yes | Yes | Yes |
| `callParentAction(actionName, params?)` | No | No | No | Yes | No |

Computed reads use the computed context's `asOf` value; they do not expose an
explicit `getPropertyAsOf` override. Neighbor direction is `"outgoing"`,
`"incoming"`, or `"both"`; omitted direction defaults to `"both"`. Host
arguments and results must remain JSON-compatible.

The only accepted write option is:

```javascript
{ validTime: "2026-07-17T00:00:00Z" }
```

`validTime` may also be `null`. `skipConstraints` and every unknown write option
are denied.

`host.callParentAction(...)` returns the parent action's ordered mutation specs.
It does not apply them. The child action must return or otherwise compose those
specs into its own result for the existing OM action pipeline to apply them.

## Limits, Cancellation, And Lifetime

Defaults are finite:

| Limit | Default |
| --- | --- |
| Wall-clock timeout | 2 seconds |
| Statements | 250,000 |
| Recursion depth | 128 |
| Memory | 32 MiB |

The same limits bound callable preflight and callback execution. The caller's
`CancellationToken` is honored during preflight, JavaScript execution, Promise
waiting, and allowlisted host work. Caller cancellation surfaces as
`OperationCanceledException`.

Provider output is immutable. Preflight uses a bounded engine, and each
callback invocation creates a fresh Jint `Engine`; JavaScript globals and
mutable engine state are never shared between invocations or threads.

## Diagnostics And Exceptions

`BuildBindings` returns deterministic `JintBehaviorScriptDiagnostic` values.
Structural graph errors, conflicts, and callable-preflight failures prevent
binding construction. A missing definition remains absent while independently
satisfied catalog bindings may still be returned; `Success` remains `false`,
and canonical readiness/import policy decides whether that partial set may be
used.

| Code | Meaning |
| --- | --- |
| `OMS1001` | Referenced script definition is missing |
| `OMS1002` | One binding ID is reused by incompatible callback shapes, or by distinct callback slots whose per-slot failure identity cannot be preserved |
| `OMS1003` | Script definitions contain a duplicate binding ID |
| `OMS1004` | Native and script callbacks conflict on the same binding ID |
| `OMS1005` | Script definition binding ID is blank |
| `OMS1006` | Script source is blank |
| `OMS1007` | Catalog callback binding ID is blank |
| `OMS1008` | Behavior kind is invalid |
| `OMS1009` | Callback slot is invalid for the behavior kind |
| `OMS1010` | Runtime option is invalid |
| `OMS1011` | A required request/catalog collection is null or uninitialized |
| `OMS1012` | A request/catalog collection contains a null element |
| `OMS1013` | Callable preflight failed, including compile or bounded-execution failure |
| `OMS1014` | Source did not evaluate to a callable function |

Callback failures use `JintBehaviorScriptException`:

| Code | Phase | Meaning |
| --- | --- | --- |
| `OMS2002` | `Execution` | JavaScript execution failed |
| `OMS2003` | `ResultConversion` | Result does not match the typed callback contract |
| `OMS2004` | `HostInvocation` | An allowlisted OM host operation failed |
| `OMS2101` | `Timeout` | Wall-clock timeout exceeded |
| `OMS2102` | `Limit` | Statement budget exceeded |
| `OMS2103` | `Limit` | Recursion depth exceeded |
| `OMS2104` | `Limit` | Memory limit exceeded |

Diagnostics and exceptions carry binding identity, behavior kind, callback
slot, failure phase, optional source name, and available line/column location.
Public messages are fixed and source-redacted; do not rely on an underlying
engine or host exception message as a public error contract.

## Import, Restart, And Transactions

Native callbacks and generated script callbacks may coexist in one
`BehaviorCallbackBindingSet`. A native callback can satisfy a canonical binding
without script source. Supplying both native and script callbacks for the same
exact binding ID is a deterministic `OMS1004` conflict and yields no bindings.
One script binding ID may be shared by compatible catalog entries only when
both callback shape and callback slot are identical. Cross-slot reuse, such as
using one constraint binding for both `When` and `Then`, is rejected with
`OMS1002` before preflight because one provider-neutral delegate cannot retain
the correct per-slot failure identity. Legal same-slot reuse produces one typed
binding that can satisfy every matching canonical entry.

`RequireReady` import remains atomic. Missing or conflicting bindings do not
partially publish metadata, callback registry state, or readiness. Callback
delegates and script source are process-local: after a database restart,
canonical behavior metadata remains persisted but callbacks are unresolved
until native callbacks and script definitions are supplied again under the same
exact IDs.

Script host writes, links, returned action mutations, inherited interceptors,
and native callbacks execute through the existing OM runtime. Existing action
transaction commit and rollback semantics therefore apply. Script failure,
host failure, returned-mutation failure, interceptor failure, timeout, or
cancellation rolls back the surrounding transaction and prevents late host
effects from being published.

## Security Boundary

This adapter provides in-process defense in depth. It is not an OS sandbox, a
process sandbox, or a security boundary for hostile untrusted code.

The adapter does not enable `AllowClr`, expose the raw OM runtime, install
module loaders, or provide filesystem or network APIs. Those omissions and the
finite resource limits reduce accidental capability exposure, but they do not
turn an in-process JavaScript engine into strong isolation. Do not execute
hostile untrusted scripts without external process isolation and an
appropriately restricted operating-system environment.
