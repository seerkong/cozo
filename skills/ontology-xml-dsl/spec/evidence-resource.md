# Evidence resources

Evidence resources own observation provenance. They do not decide business meaning.

```xml
<EvidenceModule id="FieldService.Evidence.Backend">
  <EvidenceItems>
    <Evidence
      id="backend:dispatch-qualification-check"
      repository="field-service-api"
      revision="8d3a8c5"
      path="src/main/java/example/fieldservice/DispatchPolicy.java"
      symbol="java:example.fieldservice.DispatchPolicy.validateAssignment#1"
      startLine="42"
      endLine="67"
      grade="enforced"
      resolver="treesitter"
      confidence="1.0">
      <Summary>Backend rejects dispatch when the technician lacks a required qualification.</Summary>
    </Evidence>
  </EvidenceItems>
</EvidenceModule>
```

## Grammar

- `EvidenceModule` requires FQN `id`; it contains optional `Description`, then exactly one `EvidenceItems`.
- `Evidence` requires unique namespaced `id`, `repository`, `path`, `grade`, and `confidence`.
- Optional attributes are `revision`, `symbol`, `startLine`, `endLine`, `resolver`, `observedAt`, and `sourceKind`.
- `path` is repository-relative with forward slashes. Reject absolute paths and `..` segments.
- `startLine` and `endLine` are positive integers; when both exist, `endLine >= startLine`.
- `confidence` is a decimal in `[0,1]`.
- `grade` is `authoritative|enforced|contractual|presentational|inferred`.
- `resolver` is the producing mechanism, for example `treesitter`, `roslyn`, `spring-deriver`, `regex`, `sql-schema`, `manual`, or `llm`.
- Direct children are optional `Summary` and optional `ExcerptHash`. Do not copy large source excerpts into ontology bundles.

## EvidenceRef contract

Semantic resources use `EvidenceRefs` containing one or more empty `EvidenceRef ref="evidence-id"` entries. A ref must resolve to exactly one `Evidence` in the fully assembled bundle.

- Evidence-bearing semantic objects include Type, Mixin, Attribute, ComputedAttribute, Relation, edge Property, Rule, StateMachine, Transition, ImplementationMapping, Alias, and Migration.
- Identity-bearing Type, Mixin, Relation, Rule, StateMachine, ImplementationMapping, Alias, and Migration objects require their own `EvidenceRefs`.
- An accepted interpreted object requires evidence appropriate to the strength of its claim. Child Attribute, ComputedAttribute, Property, and Transition declarations may rely on owning-object evidence only when that scope is explicit and unambiguous.
- A hypothesis requires at least one `inferred` evidence item and remains non-enforcing by default.
- Generators should emit object-local refs for independently reviewable child claims.
- `GenerationSnapshot` does not use `EvidenceRefs`; it records artifact-generation provenance and does not support ontology meaning.
- Missing refs, duplicate evidence IDs, and references to a non-Evidence semantic ID are invalid.

When the validator runs with `--generated`, owner-level coverage is not sufficient: every generated Type, Mixin, Attribute, ComputedAttribute, Relation, Property, Rule, StateMachine, Transition, ImplementationMapping, Alias, and Migration must carry direct `EvidenceRefs`. If one of these status-bearing objects is a hypothesis, at least one direct ref must resolve to `Evidence@grade="inferred"`. Default validation does not impose this generator-only strengthening on existing authored bundles.

## Java and Spring evidence

Preserve Java/Spring derivation boundaries:

- Tree-sitter symbols, structural edges, calls, and accesses identify observed code structure.
- Spring role, HTTP route, transaction, listener, handler, mapper, and DTO/Entity/VO derivations carry their original resolver and confidence.
- Naming-derived roles remain `inferred` even when repeated across many files.
- A Spring `@Transactional` fact supports a transaction-boundary interpretation but does not alone prove every domain invariant inside the method.
- Unresolved or ambiguous calls are evidence gaps, not permission to invent edges.

## Frontend and backend evidence

Frontend form validation, button visibility, and route guards are normally `presentational`. API schemas are `contractual`. Backend validation is `enforced`. Database constraints and designated system-of-record facts may be `authoritative`.

When frontend and backend disagree, retain both evidence items and record the unresolved interpretation as a hypothesis instead of choosing silently.

## Refresh and re-anchoring

Prefer stable symbol keys over lines when available. A reindex may update revision, path, line range, resolver, or confidence while preserving the evidence ID if the supported observation is semantically the same. Create a new evidence ID when the observation changes meaning.
