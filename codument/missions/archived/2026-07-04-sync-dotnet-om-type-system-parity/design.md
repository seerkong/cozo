# Mission Design: sync-dotnet-om-type-system-parity

## Control Model

- Desired state: `mission.xml` DAG, candidate TrackLinks, this design, and the reference behavior from Bun type-system specs/tests.
- Actual state: current `cozo-lib-dotnet/src/Om.Core` behavior, current `cozo-lib-bun/cozo-om.js` behavior, active/archived Codument tracks, test results, and user constraints.
- Actuation: create/execute/revise tracks, update behavior deltas, add tests, adjust .NET OM logic/facade, and write mission reports.
- Feedback/drift: failing parity tests, behavior incompatibilities, relation direction ambiguity, missing API needs, or new evidence from Bun tests.

## Mission Actors

| Actor | Cybernetic Role | DEPA Role | Responsibility |
|---|---|---|---|
| MissionPlanner | Desired-state producer | Processor + Actor | Maintains the type-system parity DAG, candidate track slicing, and acceptance gates. |
| MissionObserver | Sensor | Data + Actor | Reads Bun reference tests/specs, .NET implementation, Codument behaviors, and verification outputs. |
| MissionReconciler | Controller | Processor + Actor | Compares desired parity semantics with actual implementation and decides ready/drift/blocked/done. |
| MissionApplier | Actuator | Effect + Actor | Performs one bounded action: create a track, implement a track, run validation, or revise the mission with evidence. |

## DEPA Boundaries

- Data: Cozo stored relations (`om_type`, `om_mixin`, `om_type_mixin`, `om_attr_def`, `om_rel_def`, alias relations, `om_property`, `om_edge`), .NET DTOs/records, Bun reference fixtures.
- Effect: CozoScript execution through `ICozoOmStore`, filesystem changes to Codument and code files, test runner invocations.
- Processor: type resolution, attribute definition merging, override validation, relation endpoint validation, alias fallback mapping, `Validity` normalization.
- Actor: public `CozoOm` facade, mission actors, track execution agents.

## Reference Semantics

The mission treats the archived Bun type hierarchy track as the normative type-system design:

- mixins provide reusable attributes and do not participate in `isSubtypeOf`;
- effective attribute definitions merge in this order: mixins, far ancestors, near ancestors, self;
- self/child definitions may tighten optional to required, but cannot loosen required or change value type;
- subtype checks include identity and parent-chain ancestry;
- relation endpoint validation is subtype-aware;
- `findByType` defaults to polymorphic lookup and supports exact mode;
- schema aliases support canonical storage and compatibility with renamed data;
- `Validity` attributes accept supported temporal inputs and store Cozo validity values.

## Candidate Track Slices

1. `audit-dotnet-om-type-system-parity`
   - Write an executable parity report and failing/covering .NET tests for the known gaps.
   - Confirm whether each gap is a missing implementation, API exposure issue, or intentional semantic difference.

2. `sync-dotnet-om-attribute-semantics`
   - Implement mixin-contributed effective attributes.
   - Enforce inherited attribute override restrictions.
   - Preserve description merge semantics.
   - Expose `GetAttributeDefinitionsAsync` if needed for callers/tests.

3. `sync-dotnet-om-alias-validity-semantics`
   - Implement alias fallback/canonicalization for property reads, validation, entity views, and required-property checks.
   - Implement `Validity` attribute validation and storage parity.
   - Add public resolve APIs if required.

4. `sync-dotnet-om-type-query-redefinition`
   - Fix `DefineTypeAsync` redefinition semantics so omitted parent/mixins preserve existing metadata while explicit clearing remains possible through a clear API or input marker.
   - Add or extend polymorphic `FindByTypeAsync` filtering and `AggregateByTypeAsync` parity if confirmed missing from the .NET public surface.

5. `reconcile-om-relation-direction-parity`
   - Decide and implement/document the shared `directed=false` relation endpoint semantics.
   - Add tests in .NET and, if needed, Bun follow-up tests/documentation so both kernels have an explicit contract.

6. `verify-dotnet-om-type-system-parity`
   - Run .NET build/tests and selected Bun tests.
   - Validate Codument behavior deltas.
   - Write final parity matrix and archive-ready evidence.

## Acceptance Gates

- Each implementation track must include tests that fail before the relevant fix or prove the existing behavior.
- .NET code changes must keep `Logic/` free of direct `CozoDb`/native interop usage.
- Public API additions must delegate through `CozoOm` to `Logic` and avoid duplicating query semantics.
- Behavior deltas must clarify durable semantics before archive.
- Verification must include a gap matrix: gap, reference source, .NET file(s), test(s), result, residual risk.

## Controlled Replanning

Active mission execution may revise `mission.xml` when evidence shows a candidate track is too broad, already satisfied, or needs to split. Any replan must write `reports/replan-*.md`, increment mission revision, and explain trigger, observed state, desired state, and applied change.

## Risks

- `DefineTypeAsync` omitted-vs-explicit semantics are awkward in C# because nullable optional parameters collapse both cases; this may require an options record or a new overload.
- Alias fallback can affect validation and entity view behavior beyond simple property reads; tests must cover old alias-stored rows.
- `Validity` values depend on CozoScript `validity(...)` expression support; parameterized script construction must stay safe and predictable.
- Relation direction semantics may reveal a real Bun bug or a deliberate .NET improvement; forcing parity without a decision could regress users.
- The existing .NET tests are mostly one large program; adding focused tests may require restructuring or careful additions to avoid brittle monolith growth.
