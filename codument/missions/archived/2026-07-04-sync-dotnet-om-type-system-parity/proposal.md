# Mission: sync-dotnet-om-type-system-parity

## Background

`cozo-lib-dotnet` was ported from the Bun/Node OM work and now has the main OM skeleton: schema initialization, nominal types, attributes, relations, entities, temporal properties/edges, schema versioning, constraints, permissions, and existential rules. A focused comparison against `cozo-lib-bun` shows that the type-system kernel is not fully semantically aligned.

The gaps are concentrated in the layer above raw Cozo relations and below higher ontology behaviors:

- mixin-contributed attributes are not part of .NET effective attribute definitions;
- inherited attribute overrides do not enforce Bun's tightening-only rules;
- `Validity` is present in the .NET enum/schema but not accepted/stored as a typed property value;
- attribute alias fallback for old stored data is missing from .NET reads and validation;
- `DefineTypeAsync` cannot distinguish omitted parent/mixins from explicit clearing, so redefinition can accidentally drop hierarchy metadata;
- public inspection APIs are thinner than Bun's type-system surface;
- relation `directed=false` has drift between Bun's current implementation and .NET's reverse endpoint acceptance.

## Goal

Synchronize the `cozo-lib-dotnet` OM type-system kernel with `cozo-lib-bun` so both implementations share the same bottom-layer functional design for:

- nominal type identity, alias resolution, and canonical storage;
- single inheritance and subtype-aware behavior;
- mixin attribute reuse without mixins participating in subtype checks;
- inherited attribute definition merging and safe override constraints;
- attribute and relation definition descriptions;
- `Validity` attribute values;
- polymorphic lookup/aggregation surfaces where the Bun kernel exposes them;
- schema rename compatibility through alias fallback;
- relation direction semantics, either aligned to Bun or explicitly documented as a shared intentional semantic.

## Non-Goals

- Do not rewrite the .NET OM capsule or collapse DEPA boundaries.
- Do not port unrelated Bun features outside the type-system kernel unless needed for verification.
- Do not make Cozo stored relations subordinate to .NET object caches.
- Do not redesign the higher ontology, action, permission, or existential rule layers except where they directly depend on type-system semantics.
- Do not change `cozo-lib-bun` behavior unless the mission proves current Bun behavior contradicts its archived spec and needs an explicit follow-up track.

## Success Criteria

- .NET tests cover the Bun parity cases for mixin attributes, inherited attribute restrictions, alias fallback, `Validity` attributes, type redefinition preservation, polymorphic lookup, and relation direction reconciliation.
- `dotnet build` and the .NET OM tests pass.
- Existing Bun tests that define the reference semantics still pass, or any intentional reference change is documented with a track and updated tests.
- `cozo-dotnet-om` behavior deltas document the synchronized type-system semantics.
- Public .NET APIs expose the type-system inspection surface needed by callers without bypassing `Om.Core/Logic`.
- Final verification report maps each known gap to implemented behavior, tests, and any intentionally deferred or documented difference.

## Why This Is A Mission

This is broader than a single bug fix. It needs evidence-first parity tests, one or more .NET kernel implementation tracks, public API/doc updates, and final cross-implementation verification. Some findings may require controlled re-planning, especially relation direction semantics and the precise public surface for type-system inspection. The mission owns the route and evidence; implementation remains in tracks.
