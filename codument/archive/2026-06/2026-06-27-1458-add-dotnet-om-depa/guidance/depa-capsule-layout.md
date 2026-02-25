# DEPA Capsule Layout Guidance

This file is the implementation guidance for the C# OM track. It is intentionally stored inside the track so implementation agents do not need chat history to recover the architecture decision.

## Target Location

```text
cozo-lib-dotnet/src/Om/
```

The project already compiles `src/**/*.cs`, so this directory is part of the existing `cozo-lib-dotnet` package.

## Required Directory Shape

```text
cozo-lib-dotnet/src/Om/
  CozoOm.cs
  Contracts/
    ICozoOmStore.cs
    Models/
      OmType.cs
      OmAttribute.cs
      OmRelation.cs
      OmEntity.cs
      OmProperty.cs
      OmEdge.cs
      OmConstraint.cs
      OmComputed.cs
      OmPermission.cs
      OmSchemaVersion.cs
      OmExistentialRule.cs
  Runtime/
    CozoOmRuntime.cs
    CozoOmOptions.cs
  Inputs/
    DefineTypeInput.cs
    DefineAttributeInput.cs
    DefineRelationInput.cs
    CreateEntityInput.cs
    SetPropertyInput.cs
    LinkEntitiesInput.cs
    DefineExistentialRuleInput.cs
  Logic/
    SchemaLogic.cs
    TypeLogic.cs
    EntityLogic.cs
    RelationLogic.cs
    ConstraintLogic.cs
    TemporalLogic.cs
    PermissionLogic.cs
    SchemaVersioningLogic.cs
    ExistentialRuleLogic.cs
  Support/
    CozoDbOmStore.cs
    CozoScriptBuilder.cs
  Internals/
    JsonRows.cs
    Validation.cs
```

## Boundary

`CozoOm.cs` is the public ergonomic facade. It may expose instance methods such as `DefineTypeAsync`, `CreateEntityAsync`, and `CheckExistentialRulesAsync`, but each method should construct input/config and delegate to `Logic/*`.

`Contracts/` owns public interfaces and stable data models. It must not depend on `Logic/` or `Support/`.

`Runtime/` owns data-carrier runtime records such as `CozoOmRuntime`. Runtime classes must not contain business methods.

`Inputs/` owns request payload records. Inputs are single-call payloads, not long-lived services.

`Logic/` owns behavior. Logic functions should have signatures shaped like:

```csharp
Task<TOutput> RunAsync(CozoOmRuntime runtime, TInput input, TConfig config, CancellationToken cancellationToken = default)
```

When no static per-call config is needed, use a small empty config record or `CozoOmOptions` only when it is truly process/runtime scoped.

`Support/` owns concrete Cozo effects. Direct calls to `CozoDb.Run`, JSON row parsing tied to Cozo result shape, and CozoScript execution live here, not in `Logic/`.

`Internals/` owns private helpers. External code must not rely on these types.

## DEPA Rules For This Track

- Logic SHALL be expressible as `output = fn(runtime, input, config)`.
- `CozoOmRuntime` SHALL be a data carrier, not a service object with business methods.
- `ICozoOmStore` SHALL be the effect boundary for Cozo reads/writes.
- `Logic/` SHALL depend on `Contracts/`, `Runtime/`, and `Inputs/`, but not on `CozoDb`.
- `Support/` MAY depend on `CozoDb` and implement `ICozoOmStore`.
- Public API parity should follow Node OM behavior before adding new C#-only semantics.
- Existential rules v1 SHALL keep Node OM scope: `exists { rel, direction?, toType }`; attribute existence is non-goal.

## Fact Source Rule

Cozo stored relations are the authoritative facts for OM schema and instance assertions. C# objects returned by API calls are projections. Schema snapshots and diffs are checkpoints/projections. They must not become independent write sources.
