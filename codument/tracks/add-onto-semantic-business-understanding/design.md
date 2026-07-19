# Design: Semantic Business-Ontology Understanding

## 1. Layering and Ownership

The implementation has four layers. No layer may skip over the layer beneath it.

```text
Java/Spring + TypeScript source
        |
        v
ck_semantic_claim (source observation; reproducible, no domain assertion)
        |
        v
onto_candidate (deterministic or assisted semantic proposal; pending by default)
        |
        v
onto_review (append-only decision) + promotion materializer
        |
        v
onto_relation / onto_rule / onto_lifecycle / onto_state / onto_transition
        |
        v
business-ontology-xml v1
```

`depa_*` is outside this diagram. It is neither queried nor copied. The semantic projector may read existing `onto_*` hypothesis concepts, but it must not use raw code symbols as exported business objects.

## 2. CodeKnowledge Semantic-Claim Contract

### 2.1 Storage

Add one CodeKnowledge observation relation, owned by `Om.CodeKnowledge` and initialized with the existing schema lifecycle:

```cozoscript
:create ck_semantic_claim {claim_id =>
  subject_id, kind, payload_json, file_id, start_line, end_line,
  confidence, resolver, evidence}
```

- `claim_id`: deterministic SHA-256-derived `semantic:<hex>` over schema version, repository-relative source identity, claim kind, normalized payload, and line range.
- `subject_id`: existing `ck_symbol.symbol_id` that owns the observed declaration or method.
- `kind`: closed initial vocabulary: `typed_reference`, `validation_constraint`, `persistence_constraint`, `state_field`, `state_value`, `state_assignment`, `transaction_scope`, `route_binding`.
- `payload_json`: canonical JSON with sorted keys and no absolute paths. It is syntax/framework detail, never a business FQN or a review decision.
- `file_id`, line range, resolver, confidence, evidence: direct provenance. Annotation-derived facts use `spring_annotation`; native syntax facts use `treesitter`.

Increment `CodeKnowledgeSchema.SchemaVersion` and follow its existing explicit rebuild policy. `RepositoryIndexer` must write and file-scope-clean claims together with `ck_symbol`/`ck_edge`; migration never happens merely because ontology derivation was requested.

### 2.2 Extraction Rules

Implement the Java portion in `SpringSemanticDeriver` or a sibling source-semantic derivation component invoked from the same batch phase. Do not put Spring semantics in `JavaExtractor`.

| Claim | Direct evidence required | Normalized payload | Forbidden inference |
| --- | --- | --- | --- |
| `typed_reference` | field/record component declaration whose declared type resolves to a local candidate carrier | owner symbol, member name, raw type, resolved local type, collection flag | `String`, primitive, unresolved external type, or collection of primitive is not a business relation |
| `validation_constraint` | `@NotNull`, `@NotBlank`, `@Size`, `@Min`, `@Max`, `@Pattern`, or equivalent direct validation annotation | member, operator, value/regex, annotation name | names such as `required`, `must`, or method names alone |
| `persistence_constraint` | direct persistence annotation/column declaration exposing nullability, uniqueness, or association | member, constraint kind, direct annotation argument | database behavior inferred from an Entity suffix |
| `state_field` | field typed by a local enum and named `state`/`status` or explicitly annotated as status | owner, member, enum type | arbitrary `String status` is not a lifecycle |
| `state_value` | enum constant of a referenced state enum | enum type, value | enum names alone do not imply a lifecycle subject |
| `state_assignment` | assignment of the observed state field to a recognized enum value inside a method | owner, method, field, from value when statically known, to value | method name such as `approve` without assignment |
| `transaction_scope` | existing `@Transactional` source observation | method and annotation evidence | every service method is transactional |
| `route_binding` | existing controller route annotation/entry-point metadata | route method, HTTP method/path | route is a business rule |

Source parsing must retain declared field types and annotation arguments. If the selected parser backend cannot deliver a required observation, it emits a diagnostic and no claim. A regex-only approximation is not allowed for `state_assignment`, association, or validation facts.

## 3. Candidate Projection

### 3.1 New Components

Add these package-owned components under `Cozo.DotNet.LlmWiki.Tools`:

| Component | Responsibility |
| --- | --- |
| `BusinessOntologySemanticProjector` | Reads `ck_semantic_claim`, selected `ck_*` anchors, existing `onto_*` concepts, and optional corroboration evidence; creates candidate payloads. |
| `SemanticEvidencePackBuilder` | Builds bounded, repository-relative source/document packs for deterministic checks and assisted calls. |
| `OntologySemanticCandidateValidator` | Parses canonical JSON, validates FQNs, evidence refs, cardinality, allowed kinds, and cross-object consistency. |
| `BusinessOntologyReviewService` | Reads/writes append-only review decisions, computes latest effective decision, and materializes still-valid accepted candidates. |
| `BusinessOntologySemanticCli` | Owns CLI argument validation and maps commands to the services above. |

Add a direct project reference from `Cozo.DotNet.LlmWiki.Tools` to `Cozo.DotNet.LlmWiki.LlmClient`; no LLM API dependency belongs in `Om.Core` or CodeKnowledge.

### 3.2 Candidate Payload Schema

The candidate payload is versioned JSON, canonicalized before storage and candidate-ID computation:

```json
{
  "schemaVersion": "onto-semantic-v1",
  "kind": "relation|rule|lifecycle",
  "semantic": {
    "id": "ItAssetManagement.Ontology.Asset.supplier",
    "fromConceptId": "ItAssetManagement.Ontology.Asset",
    "toConceptId": "ItAssetManagement.Ontology.Supplier",
    "name": "supplier",
    "min": "0",
    "max": "1",
    "descriptionZh": "资产关联的供应商。"
  },
  "evidenceIds": ["evidence:..."],
  "basis": "deterministic|assisted",
  "rationale": "bounded explanation"
}
```

`rule` payloads use declarative predicate/effect JSON only. `lifecycle` payloads contain the subject concept, state property, finite state values, and transition tuples. A payload may reference only concept IDs that already exist in the current ontology generation; assisted mode may not invent new concepts in this track.

Candidate ID is computed locally from `kind`, canonical semantic payload, sorted evidence IDs, and `schemaVersion`. The model never supplies the id, confidence, status, or evidence identity.

### 3.3 Deterministic Candidate Rules

1. **Relation candidate**: a `typed_reference` from one existing concept to another creates a pending relation. A collection type gives `max="many"`; a scalar gives `max="1"`; `min` is `1` only with direct non-null/required claim, otherwise `0`.
2. **Rule candidate**: direct validation/persistence claims become pending rules such as `required`, `min`, `max`, `pattern`, `unique`. A route, service role, or transaction alone may only contribute evidence and cannot form a rule.
3. **Lifecycle candidate**: a state field plus at least two enum state values creates a pending lifecycle. A transition additionally requires a direct `state_assignment`; route/process/transaction evidence can enrich the rationale but cannot replace that assignment.
4. **Corroboration**: frontend page/form evidence and linked documentation can add direct evidence references to an existing candidate when normalized domain token and route/type anchor match. They cannot change candidate kind, cardinality, or status.
5. **Conflict handling**: mutually incompatible relation targets/cardinalities, rule predicates, or lifecycle state fields become separate candidates with a `conflict` diagnostic; neither is auto-promoted.

## 4. Assisted Proposal Mode

### 4.1 Invocation

CLI mode is explicit:

```text
depa-wiki ontology derive-semantics \
  --ontology-id ItAssetManagement.Ontology \
  --generation-id v1-semantic-<fingerprint> \
  --source-fingerprint <fingerprint> \
  --repo /path/to/backend \
  --mode deterministic|assisted \
  [--corroboration-db /path/to/frontend/.depa-wiki/depa-wiki.db] \
  [--corroboration-repo /path/to/frontend]
```

- `deterministic`: no LLM call; writes valid pending candidates only.
- `assisted`: requires `ILlmClient.IsAvailable`; otherwise fails before beginning an `onto_*` write transaction.
- A missing `ck_semantic_claim` relation produces an explicit `reindex required` diagnostic; no silent fallback to field-name heuristics.

### 4.2 Evidence Packs and Prompt

Build deterministic clusters by subject concept and candidate kind. Each cluster has at most 24 anchors, 20 KiB source text after UTF-8 truncation, and 12 surrounding lines per anchor. Include repository-relative path, line range, source excerpt, semantic claim payload, existing ontology IDs, and optional corroboration excerpts.

The system prompt requires a single JSON object with `schemaVersion="onto-semantic-v1"` and a bounded candidate list. It explicitly forbids facts not supported by supplied anchors, new concept IDs, accepted status, hidden chain-of-thought, and Markdown fences.

Parse with `JsonDocument`, reject unknown/duplicate fields and invalid values, and run `OntologySemanticCandidateValidator`. Retry at most once for malformed JSON. Any invalid response leaves no partial write; diagnostics retain only a bounded response digest/error category, not credentials or unlimited provider text.

### 4.3 LLM Trust Boundary

The LLM can propose a normalized relation name, a Chinese description, or a relation between already-established concepts. It cannot increase evidence grade, create direct evidence, accept a candidate, or bypass conflict detection. Deterministic candidate IDs ensure semantically identical valid outputs deduplicate even if wording differs.

## 5. Review and Promotion

### 5.1 Decision File and Command

Use an explicit review document:

```json
{
  "ontologyId": "ItAssetManagement.Ontology",
  "decisions": [
    {
      "candidateId": "candidate:semantic:<hash>",
      "decision": "accepted",
      "reviewer": "user:<stable-id>",
      "rationale": "确认该关联是资产的供应商关系。",
      "expectedEvidenceIds": ["evidence:..."]
    }
  ]
}
```

```text
depa-wiki ontology review apply --ontology-id ItAssetManagement.Ontology --decisions <file>
```

The command validates that each candidate is pending or previously reviewed, `expectedEvidenceIds` match the candidate's current evidence set, and a reviewer/rationale is present. It appends `onto_review`; it never edits a historical decision.

### 5.2 Effective Decision and Materialization

For every stable candidate identity, the latest valid review is the effective decision. During the next semantic generation:

- `accepted` candidates with unchanged referenced evidence materialize `accepted` `onto_relation`, `onto_rule`, lifecycle/state/transition records.
- `rejected` and `superseded` candidates remain audit-only.
- an accepted decision whose candidate identity or expected evidence set is absent is stale: do not materialize it; emit a diagnostic and require a new review.

The existing `onto_review` append-only relation remains the decision log. Extend store read APIs to expose effective reviews and semantic materialization inputs; do not delete review history on generation replacement.

## 6. XML and Artifact Policy

The existing `BusinessOntologyXmlExporter` remains the only XML writer. Once review materialization produces accepted semantic records, it emits `relations/generated.xml`, `rules/generated.xml`, and `lifecycles/generated.xml` in addition to current modules. Pending candidates remain only in `generation/candidates.json`.

The dogfood target is:

```text
/Users/kongweixian/ai/solution/it-asset-ai-solution/cozo-ontology/v1/ontology/it-asset-management/
```

It must be empty before export. `v0` is read-only baseline material for comparison and must not be overwritten.

## 7. Tests and Fixtures

### 7.1 Unit/Contract Fixtures

Create a compact Java fixture with:

- `AssetEntity` containing `@NotNull SupplierEntity supplier`, `AssetStatus status`, and a primitive/string field that must not become a relation.
- `AssetStatus` enum with at least `DRAFT`, `APPROVED`, and `REJECTED`.
- transactional methods that assign `status = APPROVED` and `status = REJECTED`.
- direct validation annotations for required/range/pattern cases.
- a controller route and a service call used only as corroborating flow evidence.
- false-positive controls: DTO-only field, string `status`, unresolved external type, collection of primitive, annotation-looking comments, and method names with no assignment.

Assert semantic claims, candidate payloads, evidence identities, reject paths, conflict diagnostics, no `depa_*` query, and deterministic re-run behavior.

### 7.2 Assisted-Mode Tests

Inject a fake `ILlmClient` for valid JSON, malformed JSON, unsupported concept IDs, unreferenced evidence, duplicate payloads, and unavailable client. Tests must prove that provider text never becomes a direct evidence record and no partial generation is written after validation failure.

### 7.3 Review/Export Tests

Apply fixture decisions accepting one candidate of each semantic kind. Assert materialized ontology records have Chinese descriptions and direct evidence refs; rejected/stale candidates do not export. Validate all generated modules with the DSL validator.

### 7.4 Real-Project Gate

Use a fresh isolated database for reindex/semantic-claim dogfood. Do not mutate either existing `.depa-wiki` database. Produce a v1 candidate report with counts and representative source anchors. Stop before promotion unless a user-provided decision file exists; an empty or missing decision file is a documented `BLOCKED` promotion gate, not a failed parser run.

## 8. Verification Commands

```bash
dotnet build cozo-lib-dotnet-llm-wiki/packages/Cozo.DotNet.LlmWiki.McpServer/Cozo.DotNet.LlmWiki.McpServer.csproj --no-restore -m:1 --nologo
dotnet build cozo-lib-dotnet-llm-wiki/tests/Cozo.DotNet.LlmWiki.Tests/Cozo.DotNet.LlmWiki.Tests.csproj --no-restore -p:BuildProjectReferences=false --nologo
dotnet run --project cozo-lib-dotnet-llm-wiki/tests/Cozo.DotNet.LlmWiki.Tests/Cozo.DotNet.LlmWiki.Tests.csproj --no-build
codument validate add-onto-semantic-business-understanding --strict
git diff --check
```

## 9. Corrected Business-Understanding Contract

### 9.1 Carrier Boundary

`DTO`, `Entity`, `VO`, `Request`, `Response`, `Query`, `Save`, `Update`, `Page`, and similar
implementation forms are mappings and evidence. A carrier can participate in a canonical concept
proposal only when at least two independent layers converge on the same domain identity, or when
an explicit domain declaration provides an authoritative anchor. One carrier alone never creates
a business concept.

Bean Validation and persistence-column annotations are attribute constraints. They may support
cardinality or a larger conditional rule, but they are not counted or exported as standalone
business rules.

### 9.2 Use-Case Slice

A bounded business use-case evidence pack joins:

1. an action boundary such as a route, message handler, scheduled business action, or frontend
   user action;
2. the service/call path implementing the action;
3. at least one repository/mapper read or write, guarded rejection, or state mutation;
4. the canonical concepts referenced by parameters, returns, accessed entities, and state values.

The slice remains source evidence. The candidate layer assigns a business name and Chinese
description only after local validation, and assisted proposals remain pending.

### 9.3 Rule and Lifecycle Semantics

A business rule requires a predicate, governed subject, and an allow/reject or state-changing
effect. Direct validation annotations alone do not satisfy this contract.

A lifecycle transition requires a state read or guard plus a state write in the same bounded
action path. The extractor must support direct assignments, setter calls, enum values, enum
codes/names, and stable string or numeric state constants. Unknown source state is retained as a
diagnostic or partial mutation claim; it cannot become a complete transition.

### 9.4 Real-Project Gate

The final report must distinguish:

- raw code carriers;
- canonical business concepts and their merged mappings;
- attribute constraints;
- conditional business rules;
- business relations;
- lifecycles and complete transitions.

The gate fails when carrier suffix pollution remains high, concepts are not consolidated,
cross-file evidence is absent, or obvious core workflows such as inbound, borrow/apply, transfer,
repair, disposal, or scrap still have no readable business semantics.

After an explicit accepted review fixture or real decision file:

```bash
/Users/kongweixian/.bun/bin/bun run \
  /Users/kongweixian/ai/solution/it-asset-ai-solution/skills/ontology-xml-dsl/scripts/validate-ontology-xml.ts \
  /Users/kongweixian/ai/solution/it-asset-ai-solution/cozo-ontology/v1/ontology/it-asset-management/ontology.xml \
  --workspace-root /Users/kongweixian/ai/solution/it-asset-ai-solution --generated
```
