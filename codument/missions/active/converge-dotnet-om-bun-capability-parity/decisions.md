# Decisions

## Status

| ID | Topic | Status | User choice |
|---|---|---|---|
| D0 | Parity standard | Resolved | A: observable semantic parity with idiomatic C# APIs |
| D1 | Permission model | Resolved | A: hard cut to Bun strict path witness and full ABAC |
| D2 | Schema migration and rollback | Resolved | B: additive strict V2 plus obsolete compatibility wrappers |
| D3 | Manifest and script runtime | Resolved | C runtime + D3a A + D3b A |
| D4 | Mature C# mechanisms | Resolved | Preserve all five identified mechanisms |
| D5 | Explicit parent clearing | Resolved | C: `Keep | Set | Clear` patch/options model |
| D6 | Registry lifecycle owner | Resolved | Runtime instance in Bun and .NET; legacy Bun functions are adapters |
| D7 | Bun callback binding portability | Resolved | Full C# V1-compatible catalog/JSON manifest/binding/readiness contract; no executable source in schema |
| D8 | Legacy snapshot missing behavior section | Resolved | C: reject by default; require explicit preserve or clear policy |

The exact user answers are preserved in [decisions/2026-07-16-parity-policy-decisions.md](decisions/2026-07-16-parity-policy-decisions.md).

## Resolved Decisions

### D0: Observable semantic parity

**Decision:** C# and Bun must align on domain effects, validation, errors, temporal interpretation, authorization outcomes, and diagnostics. C# may keep typed inputs, async/cancellation, dedicated `AsOf` methods, and other idiomatic API shapes.

**Impact:** Tests compare behavior rather than requiring identical language signatures.

### D1: Hard cut to the Bun permission model

**Decision:** Implement Bun-style strict graph path witnesses and complete entity-aware, temporal ABAC without a legacy evaluation mode. Declared paths and conditions are enforced fail-closed.

**Impact:** Existing simplified C# policies require migration. Existing wildcard selectors may only remain when they do not weaken or bypass the Bun-compatible strict evaluation contract.

**Evidence:** G06-G07 in `evidence.md`; C# currently treats paths as explanatory strings at `cozo-lib-dotnet/src/Om.Core/Logic/ConstraintLogic.cs:357-433`, while Bun evaluates witnesses at `cozo-lib-bun/cozo-om.js:1075-1116` and entity-aware ABAC at `cozo-lib-bun/cozo-om.js:1221-1408`.

### D2: Additive strict V2 schema evolution APIs

**Decision:** Add atomic, strict-by-default V2 migration and rollback APIs with structured diagnostics/results. Preserve existing APIs temporarily as obsolete compatibility wrappers. Initialization detects legacy non-temporal schemas but does not mutate them silently by default; callers use an explicit migration API or opt into automatic upgrade.

**Impact:** New code receives safe defaults and transactionality while existing callers have a migration window.

**Evidence:** G08-G11 in `evidence.md`; current C# rollback ignores `strict` and is non-atomic at `cozo-lib-dotnet/src/Om.Core/Logic/SchemaLogic.cs:164-204`, while Bun provides transactional validation and diagnostics at `cozo-lib-bun/cozo-om.js:2499-2682`.

### D3: In-process Jint runtime outside Om.Core

**Decision:** Use an in-process Jint-based JavaScript runtime, implemented in a separate package/capsule. `Om.Core` keeps the typed callback/provider abstraction and must not depend directly on Jint.

**Required controls:** cancellation, wall-clock timeout, statement/memory constraints where supported, host-object allowlisting, deterministic error mapping, and no ambient filesystem/network access.

**Evidence:** G03-G05 in `evidence.md`; C# currently uses typed delegate registries at `cozo-lib-dotnet/src/Om.Core/Runtime/CozoOmRuntime.cs:19-122`, while Bun callbacks are JavaScript functions by construction.

### D3a: Canonical JSON manifest with YAML adapter

**Decision:** JSON is the canonical manifest schema and the conformance/snapshot truth. YAML is an optional input/output adapter that must normalize to the canonical JSON model; YAML formatting is not an independent semantic truth.

**Impact:** Versioning, schema validation, checksums, fixtures, and round-trip tests use canonical JSON. YAML tests prove semantic normalization rather than byte-for-byte formatting stability.

### D3b: Import unresolved callbacks as inactive

**Decision:** A manifest may import declarative behavior metadata when callback bindings are missing. Each missing binding is explicit `inactive/unresolved`; execution fails closed with a deterministic diagnostic. A `requireReady` import mode atomically rejects the import unless every required callback is registered and compatible.

**Impact:** Metadata can move between environments without pretending to be executable, while deployment gates can demand a fully ready catalog.

### D4: Preserve all identified mature C# mechanisms

**Decision:** Preserve all five:

1. Transactional action/mutation/interceptor execution.
2. Serializable behavior metadata separated from executable delegate registration.
3. Broad C# schema snapshots, enhanced with structured diff rather than narrowed.
4. Typed async C# APIs, cancellation, and idiomatic temporal methods.
5. DEPA package boundaries, with scripting outside `Om.Core`.

**Evidence:** action transaction `cozo-lib-dotnet/src/Om.Core/Logic/ConstraintLogic.cs:72-128`; rollback test `cozo-lib-dotnet/tests/Program.cs:571-615`; registries `cozo-lib-dotnet/src/Om.Core/Runtime/CozoOmRuntime.cs:19-122`; snapshot breadth `cozo-lib-dotnet/src/Om.Core/Logic/SchemaLogic.cs:455-459`; package boundaries under `cozo-lib-dotnet/src/Om.Analytics` and `cozo-lib-dotnet/src/Om.Batch`.

### D5: Explicit parent update patch

**Decision:** Introduce a typed parent-update model with `Keep`, `Set`, and `Clear`. Preserve the current ambiguous overload during migration and deprecate it after the explicit API is available.

**Impact:** C# gains Bun's omitted-vs-explicit-clear capability without immediately changing the meaning of existing nullable arguments.

**Evidence:** G14 in `evidence.md`; C# currently treats null/blank as preserve at `cozo-lib-dotnet/src/Om.Core/Logic/TypeLogic.cs:12-25`, while Bun distinguishes omitted and explicit null at `cozo-lib-bun/cozo-om.js:3316-3328`.

### D6: Runtime-instance registry lifecycle

**Decision:** Bun and .NET use one callback registry per OM runtime instance. Both expose instance-local clear/reset. Bun legacy free functions remain compatibility adapters over one explicit legacy runtime rather than retaining an independent second registry.

**Impact:** Multiple databases/runtimes no longer leak callbacks into each other. Clearing callbacks does not delete persisted behavior definitions or callback binding identities.

### D7: Full Bun behavior portability without source persistence

**Decision:** Bun adopts the C# V1 canonical JSON behavior catalog/manifest contract, persisted callback binding identities, `unbound | unresolved | ready` projection, strict `requireReady` import, and fail-closed unresolved execution. Schema snapshot/diff/rollback includes all behavior definitions and `om_behavior_binding`. Native JavaScript function bodies and script source remain process-local and are never persisted as schema facts.

**Impact:** Behavior metadata is portable and restart-safe without pretending executable code survived restart. Native Bun callbacks, typed C# delegates, and optional script providers can satisfy the same binding identity.

### D8: Legacy snapshot behavior-section rollback policy

**Decision:** A legacy snapshot without a behavior section is incompatible with behavior-aware rollback by default. Rollback must return a structured diagnostic and make no persistent or runtime effects unless the caller explicitly selects one of two policies:

- `preserve`: retain current behavior definitions and binding identities while rolling back the other schema facts.
- `clear`: treat the missing section as an explicitly authorized empty behavior set and remove current behavior definitions and binding identities.

Snapshots using the new behavior-aware format must include an explicit behavior section, including an empty section when the historical behavior set is empty.

**Impact:** Absence remains `unknown` instead of being silently interpreted as empty or preserve. Destructive effects require an explicit caller policy. Runtime callback registries are never restored; readiness is re-derived after a successful rollback from restored/preserved binding identities and the current runtime registry.

## Enforcement

G2/G10 and D8 are resolved. G13 may proceed with fail-closed legacy rollback and explicit `preserve | clear` compatibility policy. Fresh evidence that changes the consequences of D0-D8 or D3a/D3b reopens the affected decision instead of silently overriding it.
