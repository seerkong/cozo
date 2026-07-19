# Ontology root resource

The root file is named `ontology.xml` and contains exactly one `Ontology` root.

```xml
<?xml version="1.0" encoding="UTF-8"?>
<Ontology id="FieldService" version="2.0.0">
  <Description>Field service business ontology</Description>
  <Modules>
    <TypeModule href="vfs://./types/work-orders.xml" />
    <RelationModule href="vfs://./relations/dispatch.xml" />
    <LifecycleModule href="vfs://./lifecycles/work-order.xml" />
    <RuleModule href="vfs://./rules/dispatch.xml" />
    <ImplementationMappingModule href="vfs://./mappings/backend.xml" />
    <EvidenceModule href="vfs://./evidence/backend.xml" />
    <SchemaEvolutionModule href="vfs://./evolution/schema.xml" />
  </Modules>
</Ontology>
```

## Grammar

- `Ontology` requires `id` and `version`; no other attributes are allowed.
- `id` is an ontology FQN root with at least two PascalCase segments when possible. A single established product root is allowed only when documented by the bundle.
- `version` is the non-empty, immutable semantic schema version for one coherent meaning. It is not a source revision, build number, generation counter, timestamp, or runtime schema version.
- Direct children are optional `Description` followed by exactly one `Modules`.
- `Description` contains non-empty plain text and no attributes or child elements.
- `Modules` contains one or more module references.
- Allowed references are `TypeModule`, `RelationModule`, `RuleModule`, `LifecycleModule`, `ImplementationMappingModule`, `EvidenceModule`, and `SchemaEvolutionModule`.
- A module reference has exactly one `href` attribute and no text or children.
- Every `href` resolves to a regular XML file whose root matches the reference element name.
- Duplicate hrefs are invalid.

The root only assembles resources. Do not define semantic objects inline.

At most one `SchemaEvolutionModule` is allowed in a bundle version. Its generation snapshots and each `Migration@toVersion` must name the same `Ontology@version`; `Migration@fromVersion` names an earlier explicit semantic version.
