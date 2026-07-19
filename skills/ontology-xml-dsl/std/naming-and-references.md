# Naming, identity, and references

## FQN identity

Ontology semantic IDs use dot-separated PascalCase segments with at least two segments:

```text
FieldService.WorkOrder
FieldService.Mixin.Auditable
FieldService.Relation.AssignedTo
FieldService.Rule.DispatchRequiresQualification
FieldService.Lifecycle.WorkOrder
FieldService.Evolution.V2
```

Use the stable domain name, not a Java package, TypeScript path, database table, or target slug.

Local attribute names and relation runtime names use lowerCamelCase:

```text
workOrderNumber
workflowState
assignedTo
assignedAt
```

Evidence IDs use a stable namespaced token:

```text
backend:dispatch-qualification-check
frontend:schedule-action-visibility
database:work-order-number-key
```

## VFS references

Use VFS URIs for resource files:

- `vfs://./` resolves relative to the current XML resource.
- `vfs://@/` resolves relative to the workspace root.
- Reject absolute filesystem paths, bare relative paths, backslashes, and traversal that escapes the permitted root.

Example:

```xml
<TypeModule href="vfs://./types/work-orders.xml" />
```

Semantic references use IDs, not file paths:

```xml
<EvidenceRef ref="backend:dispatch-qualification-check" />
<MixinRef ref="FieldService.Mixin.Auditable" />
```

## Reference rules

- `parent`, relation `from`/`to`, `scope`, `subject`, `typeRef`, `mixinRef`, `relationRef`, `ruleRef`, `lifecycleRef`, and evolution `targetRef` refer to semantic FQNs of the declared kind.
- `MixinRef@ref` refers only to a declared Mixin FQN.
- `EvidenceRef@ref` refers to an evidence ID.
- `ImplementationRef@ref` refers to an implementation mapping ID.
- `href` refers only to another resource file.
- Do not overload one attribute with both FQN and path semantics.

## Aliases and renames

Aliases are explicit `SchemaEvolutionModule` objects, not alternate declarations. `Alias@from` is a compatibility identity and `Alias@to` resolves to the canonical declaration of the matching kind. Preserve the canonical ID and declare an alias only when compatibility requires it.

- Type, Mixin, and Relation aliases use FQNs in both `from` and `to`.
- Attribute aliases use lowerCamelCase `from` and `to` plus `ownerRef` naming the owning Type or Mixin.
- Alias chains must resolve to one canonical declaration. Self-aliases, cycles, conflicting sources, and kind mismatches are invalid.
- A source name removed from the current module graph may appear only as `Alias@from` or a migration source; it is not a second declaration.

Never define two types with different IDs merely because frontend and backend use different DTO names; represent implementation names through mappings.

## Versioning

`Ontology@version` is the immutable semantic schema version for one coherent ontology meaning. Existing bundles may use a documented monotonic token such as `v0` or `v1`; new version lines should prefer semantic versions such as `1.2.0`. Every `Migration@fromVersion`, `Migration@toVersion`, `Alias@sinceVersion`, and `GenerationSnapshot@ontologyVersion` uses that same token vocabulary.

Start a new ontology version when changing:

- type or relation meaning;
- requiredness or cardinality in a breaking way;
- allowed state transitions;
- enforcing rule semantics; or
- identity/alias policy.

Do not change semantic version merely because source revisions, file locations, evidence lines, resolver versions, generation time, or output digest changed. Those values belong to `Evidence` or `GenerationSnapshot`. Conversely, a generation snapshot never authorizes a semantic change under an old version.
