# C# OM projection

Project validated XML to C# facade calls. The generated file is executable output, not ontology source.

## Direct mappings

| XML | C# OM facade |
| --- | --- |
| `Type` | `DefineTypeAsync` |
| Mixin declaration | `DefineMixinAsync` |
| `MixinRef` on Mixin or Type | dependency-ordered Mixin composition or `DefineTypeAsync(..., mixins: ...)` |
| Type/Mixin `Attribute` | `DefineAttributeAsync` against the owning Type or Mixin |
| `Relation` | `DefineRelationAsync` |
| Relation edge `Property` | normalized projection metadata; no current facade schema-definition equivalent |
| `ComputedAttribute` | `DefineComputedAsync` plus registered implementation |
| `Rule kind=Conditional|CrossEntity|ComputedDependency|Custom` | `DefineConstraintAsync` plus optional registered callback |
| `ExistsRelated` existential shape | `DefineExistentialRuleAsync` |
| Evidence and implementation mappings | OM interpretation entities/relations or a separate projection registry |
| Type/Relation/Attribute `Alias` | `DefineTypeAliasAsync`, `DefineRelationAliasAsync`, or `DefineAttributeAliasAsync` |
| `Migration` supported operation subset | `SchemaMigrationSpec` plus `ApplySchemaMigrationAsync` |
| `GenerationSnapshot` | projection provenance metadata, not `WriteSchemaSnapshotAsync` |

## Projection rules

- Generate in dependency order: schema init, Mixin declarations and their attributes, Types with resolved Mixin refs, Type attributes, Relations, computed declarations, constraints, existential rules, aliases, supported migrations, runtime registrations.
- Preserve XML IDs in comments or generated metadata so runtime diagnostics map back to source resources.
- Do not generate an enforcing callback for a hypothesis.
- Reject unsupported predicates instead of silently dropping them.
- Cardinality, uniqueness, and state-machine behavior require explicit compiler support or runtime bindings; do not claim enforcement from documentation alone.
- The current facade accepts relation instance props through write APIs but has no relation-property schema-definition API. Preserve edge `Property` declarations in normalized IR and projection metadata; do not emit entity instances or claim runtime validation that the facade cannot provide.
- Mixin composition must be acyclic before projection. If the target supports only Type-to-Mixin assignment, flatten Mixin-to-Mixin composition deterministically and retain source IDs in projection metadata.
- Compile only migration operations supported by the target. Unsupported Mixin aliases, relation-property changes, lifecycle changes, rule changes, and data transformations fail projection explicitly.
- Keep implementation code outside generated schema files. Generated code may register named externally owned handlers.
- After loading, compare the runtime schema snapshot against the normalized XML IR to detect projection drift.

## Version and provenance mapping

`Ontology@version` is a semantic string token. Cozo OM schema migration and snapshot APIs currently use positive integer versions. A projection must receive or persist an explicit deterministic semantic-version-to-runtime-version mapping; it must not parse timestamps, commits, `GenerationSnapshot@id`, or source revisions into a runtime version.

`GenerationSnapshot` is not projected through `WriteSchemaSnapshotAsync`. The XML snapshot records generation provenance, while the OM snapshot records materialized runtime schema state. Store their correlation as projection metadata when needed.

## Example

```csharp
// Generated from types/work-orders.xml: FieldService.WorkOrder
await om.DefineTypeAsync(
    "FieldService.WorkOrder",
    "An accepted request for field work",
    parentType: "FieldService.WorkItem",
    mixins: ["FieldService.Mixin.Auditable"],
    cancellationToken: cancellationToken);

await om.DefineAttributeAsync(
    "FieldService.WorkOrder",
    "workOrderNumber",
    OmValueType.String,
    required: true,
    cancellationToken: cancellationToken);
```

Generated names may be adapted to runtime naming constraints, but the mapping must be deterministic and reversible.
