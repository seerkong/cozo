# Proposal: Add .NET OM Core Parity APIs

## Problem

The C# OM implementation has diverged from the Bun OM reference in a small but important part of the core object-system semantics.

The gap is not in the broad schema/entity/relation foundation; C# already has type hierarchy, mixins, aliases, temporal properties, temporal edges, validation-on-write, and entity views. The missing parts are the public and behavioral parity around complete historical entity views, property-filtered type search, explicit validation helpers, computed inheritance, and typed runtime constraints.

## Goals

- Add a public C# API for historical entity views equivalent to Bun `getEntityViewAsOf`.
- Add a property-filtered type search path that returns entity properties while preserving the current lightweight `FindByTypeAsync` contract.
- Expose public validation/finalization helpers equivalent to Bun's core validation APIs.
- Align computed-property inheritance and child override behavior with Bun.
- Align runtime constraint scopes with Bun for `conditional`, `cross-entity`, and `computed-dep` semantics.
- Add tests in both C# and Bun so the reference behavior and parity behavior stay visible.

## Non-Goals

- Do not redesign OM storage relations.
- Do not add arbitrary Datalog rule heads to existential rules.
- Do not make persisted callback metadata restore .NET delegates after process restart.
- Do not change the existing meaning or return type of the current lightweight `FindByTypeAsync` method unless a backward-compatible overload is used.
- Do not move CodeKnowledge or DEPA ontology behavior in this track.

## Proposed Changes

### Historical Entity View

Add `GetEntityViewAsOfAsync(entityId, asOf)` to C# OM. It should:

- Read the entity id/type/label from `om_entity`.
- Resolve the canonical type.
- Read `om_property` at `@ $as_of`.
- Canonicalize property aliases.
- Include inherited computed properties evaluated through an as-of-aware context.
- Read outgoing neighbors through `GetNeighborsAsOfAsync(..., OmDirection.Outgoing)`.
- Return the existing `EntityView` model shape.

### Filtered Type Search

Add a property-filtered search API without breaking existing `FindByTypeAsync`.

The conservative design is to add a new result model, for example `FindByTypeEntry`, and either:

- overload `FindByTypeAsync` with a filter input returning rich entries, or
- add `FindByTypeWithPropertiesAsync`.

The behavior should match Bun:

- `Exact=false` includes descendants.
- `Exact=true` only uses the canonical requested type.
- Filters use equality against current canonicalized properties.
- Results include `id`, `typeName`, `label`, and `properties`.

### Validation Facade

Expose public C# methods for:

- property type preflight validation
- relation endpoint preflight validation
- required-property validation
- finalize entity validation

`FinalizeEntityAsync` should run the same required/type/constraint validation used by `ValidateEntityAsync`, and fail with diagnostics if invalid.

### Computed Inheritance

Computed definitions and runtime delegates should resolve through the effective type chain:

- parent computed properties are inherited by child types
- child definitions override parent definitions with the same attribute name
- `GetPropertyAsync`, `GetPropertyAsOfAsync`, `GetEntityViewAsync`, and `GetEntityViewAsOfAsync` use the same inherited lookup

### Constraint Scope Parity

Extend C# runtime constraints beyond current exact-type `custom` validators:

- support `conditional`
- support `cross-entity`
- support `computed-dep`
- keep `custom` as a backward-compatible alias or supported scope
- apply inherited constraints from ancestors with child overrides
- preserve rollback behavior when validation fails during property or relation writes

## Compatibility

Existing C# callers should continue compiling. The existing `FindByTypeAsync(string, FindByTypeOptions?)` returning `IReadOnlyList<OmEntity>` should remain available.

New APIs should be additive or overload-compatible. Existing `custom` validators should continue to work.

## Acceptance

- .NET tests cover all behavior delta cases.
- Bun tests continue covering the same reference behavior.
- `dotnet test` for `cozo-lib-dotnet/tests` passes.
- `bun test` for `cozo-lib-bun` passes or any environment limitation is documented.
- `codument validate add-dotnet-om-core-parity-apis --strict` passes when the CLI is available.
