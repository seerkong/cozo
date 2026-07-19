# Standard ontology bundle

An ontology bundle is a modular, versioned XML source tree. The root assembles modules; modules own facts; generated files live outside source modules.

## Layout

```text
<bundle-root>/
  ontology.xml
  types/
    work-orders.xml
    technicians.xml
    customers.xml
  relations/
    dispatch.xml
  rules/
    dispatch.xml
    scheduling.xml
  lifecycles/
    work-order.xml
  mappings/
    backend.xml
    frontend.xml
    api-crosswalk.xml
  evidence/
    backend.xml
    frontend.xml
  evolution/
    schema.xml
  generated/
    FieldServiceOntology.g.cs
  docs/
    domain-model.md
    rule-catalog.md
    uncertainties.md
```

Only XML under the root and semantic module directories is canonical ontology source. `generated/` and `docs/` are projections.

## Ownership

| Resource | Owns | Must not own |
| --- | --- | --- |
| `ontology.xml` | Ontology identity, version, description, ordered module references | Type, relation, rule, transition, code path, or evidence definitions |
| `TypeModule` | Mixin declarations and composition, types, inheritance, entity/mixin attributes, computed declarations | Cross-type relation definitions, rule predicates, code mappings |
| `RelationModule` | Relation identity, endpoints, direction, cardinality, edge-owned properties | Runtime callback, entity attributes, or an identity-bearing association |
| `RuleModule` | Rule identity, scope, typed predicates, violations, evidence references | Source coordinates or host-language implementation bodies |
| `LifecycleModule` | State machines, states, guards, transitions, declarative effects | Backend method names or UI components |
| `ImplementationMappingModule` | Domain-to-code interpretation links | Business type definitions or copied code graph facts |
| `EvidenceModule` | Repository/revision/source location, resolver, grade, confidence | Business interpretation or rule meaning |
| `SchemaEvolutionModule` | Compatibility aliases, declarative semantic migrations, generation snapshots | Duplicate current declarations, executable migration code, runtime data, or implicit version bumps |
| `generated/*.g.cs` | Derived C# OM facade projection | Author-maintained ontology meaning |

## Module sizing

Split modules by cohesive domain responsibility, not by file size alone. Prefer one type module per bounded concept family and one rule module per workflow or policy family. Avoid one file per attribute and avoid a single repository-wide XML file.

## Assembly order

Declare modules in dependency order:

1. Type modules.
2. Relation modules.
3. Lifecycle modules.
4. Rule modules.
5. Implementation mappings.
6. Evidence modules.
7. Schema evolution modules after the declarations and evidence they reference.

Loaders must resolve the full graph before rejecting forward semantic references; textual order is not semantic order.

## Source and projection

Use this one-way flow:

```text
ontology XML -> normalized ontology IR -> C# OM facade projection -> om_* runtime data
                                      -> Markdown/diagram/RDF projections
```

Do not reverse-generate canonical XML from mutated runtime rows without an explicit reconciliation workflow. Runtime snapshots may be compared against XML to detect drift.

`Ontology@version` travels with normalized semantic IR. `GenerationSnapshot` travels as provenance metadata. A runtime schema snapshot and its integer counter are projection state, not substitutes for either XML concept.

## Evidence lifecycle

Code indexing may refresh file paths, lines, commits, and resolver details without changing semantic IDs or semantic version. Keep evidence changes isolated so a reindex does not rewrite type, rule, lifecycle, or evolution declarations when meaning is unchanged. A reproducible regeneration may append or replace generation provenance for the same semantic version.

## Hypotheses

A semantic object may use `status="hypothesis"` while evidence is incomplete. Hypotheses must:

- have a `Statement` or `Description` that names the uncertainty;
- reference at least one `inferred` evidence item;
- not be projected as an enforcing runtime constraint by default; and
- be listed in the generated uncertainty report.

Use `status="accepted"` or omit the attribute for accepted facts. Do not use confidence alone as acceptance state.

## Forbidden dependencies

Canonical modules may use source observations and `ck_*` evidence through `Evidence` and `ImplementationMapping`. They must not import, resolve, reference, or emit `depa_*` objects. Host-language code, callbacks, scripts, and entity instances remain outside the bundle.
