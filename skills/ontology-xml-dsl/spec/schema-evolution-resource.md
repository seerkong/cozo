# Schema evolution resources

A `SchemaEvolutionModule` owns compatibility aliases, declarative migrations between semantic schema versions, and reproducible generation provenance. It does not redefine current semantic objects and does not contain executable migration code.

```xml
<SchemaEvolutionModule id="FieldService.Evolution.V2">
  <Description>Compatibility and provenance for semantic version 2.0.0</Description>
  <Aliases>
    <Alias
      id="FieldService.Evolution.Alias.ServiceTicket"
      kind="type"
      from="FieldService.ServiceTicket"
      to="FieldService.WorkOrder"
      sinceVersion="2.0.0">
      <EvidenceRefs>
        <EvidenceRef ref="decision:rename-service-ticket" />
      </EvidenceRefs>
    </Alias>
    <Alias
      id="FieldService.Evolution.Alias.WorkOrderState"
      kind="attribute"
      ownerRef="FieldService.WorkOrder"
      from="state"
      to="workflowState"
      sinceVersion="2.0.0">
      <EvidenceRefs>
        <EvidenceRef ref="decision:rename-work-order-state" />
      </EvidenceRefs>
    </Alias>
  </Aliases>
  <Migrations>
    <Migration
      id="FieldService.Evolution.Migration.V1ToV2"
      fromVersion="1.4.0"
      toVersion="2.0.0"
      compatibility="breaking">
      <Description>Rename the work request identity and its state property.</Description>
      <Changes>
        <Rename kind="type" from="FieldService.ServiceTicket" to="FieldService.WorkOrder" />
        <Rename
          kind="attribute"
          ownerRef="FieldService.WorkOrder"
          from="state"
          to="workflowState" />
      </Changes>
      <EvidenceRefs>
        <EvidenceRef ref="decision:v2-schema-migration" />
      </EvidenceRefs>
    </Migration>
  </Migrations>
  <GenerationSnapshots>
    <GenerationSnapshot
      id="generation:field-service-2.0.0-20260717"
      ontologyVersion="2.0.0"
      generatedAt="2026-07-17T08:00:00Z"
      generator="code-to-ontology-xml"
      generatorVersion="1">
      <SourceRevisions>
        <SourceRevision repository="field-service-api" revision="8d3a8c5" />
        <SourceRevision repository="field-service-web" revision="c14b620" />
      </SourceRevisions>
      <OutputDigest algorithm="sha256" value="2f1a6f03b8e894d6239aef31ba77dc42c4b2f99e70ea89b1e56bca6f5ef7d013" />
    </GenerationSnapshot>
  </GenerationSnapshots>
</SchemaEvolutionModule>
```

## Module grammar

- `SchemaEvolutionModule` requires FQN `id`.
- Direct children are optional `Description`, optional `Aliases`, optional `Migrations`, and optional `GenerationSnapshots`, in that order. At least one of the three containers is required.
- A bundle contains at most one `SchemaEvolutionModule`.
- The module may reference declarations and evidence from the fully resolved bundle. It must not duplicate Type, Mixin, Attribute, Relation, Rule, Lifecycle, Mapping, or Evidence declarations.

## Alias grammar

- `Aliases` contains one or more `Alias` elements.
- `Alias` requires FQN `id`, `kind`, `from`, `to`, and `sinceVersion`. Optional `untilVersion` bounds compatibility.
- Optional `status` is `accepted|hypothesis`.
- `kind` is `type|mixin|relation|attribute`.
- For `type`, `mixin`, and `relation`, `from` and `to` are FQNs and `ownerRef` is forbidden.
- For `attribute`, `from` and `to` are lowerCamelCase names and `ownerRef` is required; the owner resolves to a Type or Mixin and `to` resolves to its current Attribute.
- Direct children are optional `Description` then optional `EvidenceRefs`.
- `to` must resolve to a current canonical declaration of the declared kind. `from` is a compatibility identity and may name a declaration absent from the current bundle.
- Self aliases, alias cycles, duplicate `kind+ownerRef+from` sources, conflicting targets, and kind mismatches are invalid.

Aliases preserve lookup compatibility. They do not assert that two concurrently declared business concepts are equivalent, and they never replace an implementation mapping for DTO, table, route, or UI names.

## Migration grammar

- `Migrations` contains one or more `Migration` elements.
- `Migration` requires FQN `id`, `fromVersion`, `toVersion`, and `compatibility="backward-compatible|breaking"`.
- Optional `status` is `accepted|hypothesis`.
- Version endpoints use the same semantic-version token vocabulary as `Ontology@version`, differ from each other, and form an acyclic migration graph. `toVersion` equals the containing root `Ontology@version`.
- Direct children are optional `Description`, exactly one `Changes`, then optional `EvidenceRefs`.
- `Changes` contains one or more typed operations: `Add`, `Remove`, `Rename`, or `Alter`.
- `Add` and `Remove` require `kind="type|mixin|attribute|relation|rule|lifecycle"` and `targetRef`. An Attribute target uses `ownerRef` plus lowerCamelCase `targetRef`; other targets use FQNs.
- `Rename` requires `kind="type|mixin|attribute|relation"`, `from`, and `to`; Attribute renames also require `ownerRef`.
- `Alter` requires `kind="attribute|requiredness|cardinality|lifecycle|rule"`, `targetRef`, and `aspect`; it describes the changed semantic aspect without embedding an expression or data transformation.
- Operations reference declarations owned by the endpoint bundles; they do not copy those declarations into the migration. For the current bundle, `Add@targetRef`, `Rename@to`, and `Alter@targetRef` resolve to current declarations of the stated kind. Historical `Remove@targetRef` and `Rename@from` names may be absent from the current graph but must be well-formed and consistent with aliases when an alias exists.

A Migration is a declarative semantic change plan. It must not contain scripts, callbacks, query text, host-language bodies, entity data, or arbitrary transformation expressions. A projection may compile only the operation subset its runtime supports and must reject the rest explicitly.

## Generation snapshot grammar

- `GenerationSnapshots` contains one or more `GenerationSnapshot` elements.
- `GenerationSnapshot` requires unique namespaced `id`, `ontologyVersion`, RFC 3339 `generatedAt`, and non-empty `generator`. Optional `generatorVersion` identifies the generator release.
- `ontologyVersion` must equal the containing root `Ontology@version`.
- Direct children are exactly one `SourceRevisions`, then exactly one `OutputDigest`.
- `SourceRevisions` contains one or more `SourceRevision` entries with unique stable `repository` keys and immutable non-empty `revision` values.
- `OutputDigest` requires `algorithm="sha256"` and a lowercase 64-hex `value`. The digest covers normalized semantic IR and excludes generation-provenance metadata itself, avoiding a self-referential digest.
- Generation snapshots do not use `EvidenceRefs`: they are provenance records, not semantic interpretations.

Multiple generations may share one semantic version when source anchors, evidence coordinates, generator release, or normalized serialization change without changing ontology meaning. A semantic change requires a new `Ontology@version` even when the generator and source revisions are unchanged.

## Boundary rules

- Semantic version answers "what ontology meaning is this?"
- Generation provenance answers "from which revisions, by which generator, and when was this artifact produced?"
- Runtime schema snapshots answer "what projection state is materialized?"

These identities are related only by explicit projection metadata. Never derive semantic version from a commit, timestamp, generation ID, digest, or runtime integer. The module must not reference or emit `depa_*` objects.
