# Evidence Registry

## Scope And Method

This registry captures the two analysis waves that motivated the mission. Evidence was read from the current checkout on 2026-07-16. Line references are evidence anchors, not substitutes for refreshing the code before implementation.

The comparison used:

- `cozo-lib-bun/README.md` and the Bun implementation/tests;
- `cozo-lib-dotnet/src/Om.Core` and `cozo-lib-dotnet/tests`;
- archived tracks `2026-02-28-add-type-hierarchy`, `2026-03-01-add-action-and-constraints`, `2026-03-01-add-temporal-dimension`, `2026-03-02-add-schema-versioning-permission-integration`, and `2026-06-10-0945-add-existential-rules`;
- the completed mission `codument/missions/archived/2026-07-04-sync-dotnet-om-type-system-parity` to avoid reopening already-closed type-system work without fresh evidence.

The first-wave portability items G03-G05 are not claims that Bun already has a portable manifest/import format. They are capability requirements derived from Bun's native JavaScript callback model: C# needs explicit mechanisms to reach comparable configurability and observability.

## First-Wave Evidence

### G01: Missing parent-action dispatch in C#

**Status:** Confirmed direct parity gap.

- Bun resolves the current action owner, walks to the parent owner, and exposes `callParentAction` through action context at `cozo-lib-bun/cozo-om.js:448-507` and `cozo-lib-bun/cozo-om.js:623`.
- Bun tests subtype execution and override extension through parent dispatch at `cozo-lib-bun/__tests__/om-action.test.js:83-176`, including `ctx.callParentAction(...)` at lines 133 and 171.
- C# action context exposes `ActionOwnerType` but no parent-call facility at `cozo-lib-dotnet/src/Om.Core/Runtime/CozoOmRuntime.cs:179-185`; the facade execution surface at `cozo-lib-dotnet/src/Om.Core/CozoOm.cs:241` has no `CallParentActionAsync` equivalent.

**Required outcome:** Add an async parent-action API/context capability with cycle/end-of-chain diagnostics and transactional composition.

### G02: Inheritance behavior is not equivalently tested

**Status:** Confirmed test and contract gap; some C# resolution logic already exists.

- Bun tests parent action inheritance and override/parent composition at `cozo-lib-bun/__tests__/om-action.test.js:83-176`.
- Bun tests parent interceptor application to a subtype at `cozo-lib-bun/__tests__/om-interceptor.test.js:98-114`.
- C# resolves inherited behavior definitions in `cozo-lib-dotnet/src/Om.Core/Logic/ConstraintLogic.cs:561-606`.
- Current C# behavior tests at `cozo-lib-dotnet/tests/Program.cs:521-615` cover same-type mutation/action/interceptor ordering and rollback, but not parent action inheritance, parent dispatch, or inherited interceptors.
- C# reverses the owner chain and then globally orders all collected interceptors by each owner's local `Seq` at `cozo-lib-dotnet/src/Om.Core/Logic/ConstraintLogic.cs:595-606`; this can interleave parent and child interceptors. Bun preserves ancestor-to-child owner order and registration order within each owner at `cozo-lib-bun/cozo-om.js:560-581`.

**Required outcome:** Add paired Bun/C# scenarios for inherited actions, mutation lookup, interceptor order, override, parent dispatch, and rollback.

### G03: No explicit portable behavior catalog and callback status round-trip

**Status:** Confirmed operational capability gap.

- C# persists behavior metadata and snapshots it at `cozo-lib-dotnet/src/Om.Core/Logic/SchemaLogic.cs:455-459`.
- C# executable callbacks live in private runtime dictionaries with register/resolve operations but no catalog export/import at `cozo-lib-dotnet/src/Om.Core/Runtime/CozoOmRuntime.cs:19-122`.
- Bun explicitly treats handlers as runtime callbacks rather than persisted values at `cozo-lib-bun/cozo-om.js:8`, with behavior metadata relations initialized near `cozo-lib-bun/cozo-om.js:716`.

**Required outcome:** Export/import definitions separately from executable bindings; expose `registered`, `missing`, and incompatible callback state with diagnostics.

### G04: No optional JSON/YAML behavior manifest

**Status:** Confirmed requested extension for parity of configurability.

- Bun defines mutations and actions through JavaScript registration functions at `cozo-lib-bun/cozo-om.js:380-431`.
- A search for `manifest|yaml|yml` in `cozo-lib-dotnet/src/Om.Core` and `cozo-lib-bun/cozo-om.js` returned no OM manifest implementation on 2026-07-16.

**Required outcome:** Define a versioned, optional manifest for metadata and callback binding references. The format and source-of-truth rules require user decision D3.

### G05: No optional script runtime for C# behavior callbacks

**Status:** Confirmed requested extension; runtime choice is intentionally unresolved.

- Bun callbacks are JavaScript functions by construction (`cozo-lib-bun/cozo-om.js:8`, action/mutation definitions at lines 380-431).
- C# registers typed delegates in `cozo-lib-dotnet/src/Om.Core/Runtime/CozoOmRuntime.cs:21-27` and `:65-88`; no Jint, JavaScript host, or generic script adapter exists in the OM core.

**Required outcome:** Add an optional adapter outside `Om.Core`; isolation/process/runtime choice is user decision D3.

## Second-Wave Evidence

### G06: C# permission paths are explanatory strings, not authorization witnesses

**Status:** Confirmed security-sensitive parity gap.

- C# finds matching policies before evaluating graph paths at `cozo-lib-dotnet/src/Om.Core/Logic/ConstraintLogic.cs:357-433`; `PermissionPathsAsync` only reads configured path strings at `ConstraintLogic.cs:651-662`.
- The C# test at `cozo-lib-dotnet/tests/Program.cs:625-629` accepts a path string in the explanation without creating a subject-to-resource graph witness.
- Bun evaluates path clauses as graph witnesses at `cozo-lib-bun/cozo-om.js:1075-1116` and uses the result in access evaluation at `cozo-lib-bun/cozo-om.js:1299-1329`.

**Required outcome:** Evaluate declared paths against the actual graph and fail according to user decision D1.

### G07: C# ABAC lacks entity attributes, temporal evaluation, comparators, field hide, and detailed explanations

**Status:** Confirmed direct parity gap with compatibility consequences.

- C# ABAC at `cozo-lib-dotnet/src/Om.Core/Logic/ConstraintLogic.cs:620-675` resolves simple references and only supports `=`, `==`, and `!=`.
- `CheckAccessInput.AsOf` exists at `cozo-lib-dotnet/src/Om.Core/Models/OmModels.cs:161` but is not used in the C# evaluation path.
- Bun resolves attributes at a temporal point at `cozo-lib-bun/cozo-om.js:1221-1250`, supports richer comparisons at `cozo-lib-bun/cozo-om.js:1054-1072`, and returns field-hide/explanation detail at `cozo-lib-bun/cozo-om.js:1331-1408`.

**Required outcome:** Add entity-aware temporal ABAC and field-level results while preserving or deliberately migrating useful C# wildcard matching under D1.

### G08: C# schema rollback ignores strictness and is not atomic

**Status:** Confirmed reliability gap.

- C# rollback at `cozo-lib-dotnet/src/Om.Core/Logic/SchemaLogic.cs:164-204` accepts `strict` but does not use it, returns no structured result, and clears/rebuilds state without one transaction boundary.
- Bun rollback at `cozo-lib-bun/cozo-om.js:2499-2682` performs entity validation, supports strict/force behavior, reports diagnostics, and applies changes transactionally.

**Required outcome:** User decision D2 must freeze API/default compatibility; implementation must provide agreed validation, diagnostics, and atomicity.

### G09: C# initialization does not upgrade legacy non-temporal relations

**Status:** Confirmed compatibility gap.

- C# initialization creates current relations at `cozo-lib-dotnet/src/Om.Core/Logic/SchemaLogic.cs:13-53`; create-conflict handling at `cozo-lib-dotnet/src/Om.Core/Logic/LogicSupport.cs:11-20` does not inspect or migrate old relation shapes.
- Bun performs temporal relation migration at `cozo-lib-bun/cozo-om.js:89-118` and detects/initializes legacy schemas at `cozo-lib-bun/cozo-om.js:760-788`.
- Bun regression coverage is in `cozo-lib-bun/__tests__/om-temporal-migration.test.js:7` onward.

**Required outcome:** Detect legacy shapes, upgrade safely and idempotently, and test existing data preservation.

### G10: C# migration strict preflight and atomicity are incomplete

**Status:** Confirmed reliability and default-contract gap.

- C# migration input defaults `Strict=false` at `cozo-lib-dotnet/src/Om.Core/Inputs/OmInputs.cs:57-64`.
- C# apply logic at `cozo-lib-dotnet/src/Om.Core/Logic/SchemaLogic.cs:206-275` is not wrapped as one transaction; strict validation near `SchemaLogic.cs:349-368` does not provide Bun-equivalent preflight behavior.
- Bun defaults strict migration on at `cozo-lib-bun/cozo-om.js:1458-1467`, executes in a transaction at `cozo-lib-bun/cozo-om.js:2161`, and performs strict preflight at `cozo-lib-bun/cozo-om.js:2327` onward.
- Bun's strict rejection scenario is tested at `cozo-lib-bun/__tests__/om-schema-migration-apply.test.js:166` onward.

**Required outcome:** Decide compatibility under D2, then implement preflight and all-or-nothing apply.

### G11: C# schema diff is coarse-grained

**Status:** Confirmed observability gap; C# snapshot breadth should be preserved.

- C# diff at `cozo-lib-dotnet/src/Om.Core/Logic/SchemaLogic.cs:492-499` compares whole snapshots instead of returning per-definition changes.
- Bun creates keyed added/removed/updated differences per schema table at `cozo-lib-bun/cozo-om.js:2000-2099`.
- C# snapshots include behavior definitions at `SchemaLogic.cs:455-459`, which is useful additional coverage.

**Required outcome:** Add structured keyed diff over the broader C# snapshot rather than narrowing it.

### G12: C# existential rules miss define-time operator validation and runtime alias re-resolution

**Status:** Confirmed governance and compatibility gap.

- C# definition validation at `cozo-lib-dotnet/src/Om.Core/Logic/ExistentialRuleLogic.cs:155-184` does not whitelist `where` operators.
- C# evaluation at `ExistentialRuleLogic.cs:187-244` uses stored relation/attribute names without re-resolving current aliases; list behavior at `ExistentialRuleLogic.cs:60-69` throws when storage is absent.
- Bun validates definitions at `cozo-lib-bun/cozo-om.js:5266-5359`, re-resolves canonical names and aliases at `cozo-lib-bun/cozo-om.js:5399-5428`, and returns safely before initialization at `cozo-lib-bun/cozo-om.js:5607-5623`.
- Bun tests alias/version behavior at `cozo-lib-bun/__tests__/om-existential-versioning.test.js:154` and invalid definitions at `cozo-lib-bun/__tests__/om-existential-define.test.js:140`.

**Required outcome:** Reject unsupported predicates before persistence, resolve aliases at evaluation time, and make inspection safe before initialization.

### G13: C# behavior definition and callback registration lack guardrails

**Status:** Confirmed correctness gap.

- Bun verifies behavior owner types through `_ensureBehaviorTypeExists` at `cozo-lib-bun/cozo-om.js:172-178` and invokes it from behavior definition paths.
- C# writes behavior metadata without equivalent owner/scope validation at `cozo-lib-dotnet/src/Om.Core/Logic/ConstraintLogic.cs:13-68`.
- C# normalizes known constraint scope aliases but retains unknown scope strings at `cozo-lib-dotnet/src/Om.Core/Logic/ConstraintLogic.cs:546-559`, so invalid behavior scope metadata can be persisted instead of rejected.
- `CozoOm.AddInterceptorAsync` registers the callback before persistent validation at `cozo-lib-dotnet/src/Om.Core/CozoOm.cs:229-239`; registry logic at `cozo-lib-dotnet/src/Om.Core/Runtime/CozoOmRuntime.cs:91-110` treats any non-`after` phase as `before`, allowing invalid metadata to leave a ghost callback.

**Required outcome:** Validate type/scope/phase first and make metadata plus callback registration atomic or compensating.

### G14: Lower-level public API capabilities remain missing

**Status:** Confirmed surface gap, with C#-idiomatic API shape allowed.

- Explicit parent clearing: C# `DefineTypeAsync` preserves an existing parent when passed null at `cozo-lib-dotnet/src/Om.Core/Logic/TypeLogic.cs:12-25`; Bun distinguishes omitted from explicit null at `cozo-lib-bun/cozo-om.js:3316-3328`.
- Constraint facade: C# constraint logic exists at `cozo-lib-dotnet/src/Om.Core/Logic/ConstraintLogic.cs:192-211`, but `cozo-lib-dotnet/src/Om.Core/CozoOm.cs` has no public `ValidateConstraints` facade.
- Traversal: Bun exposes traversal at `cozo-lib-bun/cozo-om.js:4693-4725` and `cozo-lib-bun/cozo-om.d.ts:646`; C# has no equivalent general traversal facade.
- Value inference: Bun exposes `inferValueType` at `cozo-lib-bun/cozo-om.d.ts:364`; C# inference is internal at `cozo-lib-dotnet/src/Om.Core/OmConvert.cs:44`.

**Required outcome:** Add behaviorally equivalent public APIs with typed options and cancellation, without copying JavaScript signatures mechanically.

## C# Advantages Requiring User Confirmation

The following C# capabilities are evidence-backed candidates to preserve. They are not pre-approved decisions; mission task G2 and decision D4 require the user's confirmation before affected implementation begins:

- Transactional action/mutation/interceptor execution: `cozo-lib-dotnet/src/Om.Core/Logic/ConstraintLogic.cs:72-128` and rollback test `cozo-lib-dotnet/tests/Program.cs:571-615`.
- Explicit typed callback registries: `cozo-lib-dotnet/src/Om.Core/Runtime/CozoOmRuntime.cs:19-122`.
- Broader snapshots including behavior metadata: `cozo-lib-dotnet/src/Om.Core/Logic/SchemaLogic.cs:455-459`.
- Separate analytics and batch packages rather than core coupling: `cozo-lib-dotnet/src/Om.Analytics/CozoOmAnalyticsExtensions.cs:10-91` and `cozo-lib-dotnet/src/Om.Batch`.
- Existing computed-property inheritance coverage: `cozo-lib-dotnet/tests/Program.cs:408-417`.

The recommended route is to port missing semantics into the current C# architecture rather than treating Bun source structure as the desired design. The mission executor must present that recommendation and its consequences to the user before applying it.

## Baseline Verification

- On 2026-07-16, `MSBUILDTERMINALLOGGER=off dotnet run --project cozo-lib-dotnet/tests/Cozo.DotNet.Om.Tests.csproj` completed with `Cozo.DotNet OM tests passed.`
- The Bun executable was not available in the current shell (`zsh: command not found: bun`), so current Bun suite execution was not independently re-run. Existing Bun test files are source evidence only until the final mission can run them.
