# New-Session Execution Brief

## Objective

Implement `add-onto-semantic-business-understanding` exactly as specified in `proposal.md`, `design.md`, and `behavior_deltas/dotnet-llm-wiki/delta.xml`.

## Start Here

1. Read this brief, proposal, design, analysis files, the parent mission replan report, and `BusinessOntologyCandidateDeriver.cs`.
2. Check `git status --short`; preserve the concurrent Bun/.NET OM parity work and unrelated LlmWiki changes.
3. Run `codument validate add-onto-semantic-business-understanding --strict` before edits.
4. Do not alter `cozo-lib-bun/**`, `cozo-lib-dotnet/src/Om.Core/**`, or `cozo-lib-dotnet/tests/**` for this track.

## Execution Waves

### Wave 1: CodeKnowledge source claims

- Extend `CodeKnowledgeSchema`, `CodeKnowledgeBatch`, persistence/cleanup, and `RepositoryIndexer` for `ck_semantic_claim`.
- Implement Java/Spring source claim extraction beside `SpringSemanticDeriver`, retaining source anchors and diagnostics.
- Add migration/preflight tests. Existing databases must require explicit reindex, never be mutated by ontology commands.
- Gate: source claim fixture passes and deletion/incremental refresh removes stale claims.

### Wave 2: Deterministic ontology candidates

- Add semantic model/validator/evidence-pack classes under LlmWiki Tools.
- Read claims plus existing `onto_*` concepts; generate pending relation/rule/lifecycle candidates only under the rules in design section 3.3.
- Keep the old structural derivation behavior compatible; compose semantic projection after it rather than weakening its DEPA isolation.
- Gate: true positives, false positives, conflict, and repeatability tests pass.

### Wave 3: Assisted structured proposals

- Add the direct `Tools -> LlmClient` reference and inject `ILlmClient`; do not construct clients deep in the projector.
- Implement strict JSON request/response schema, size bounds, local validation, one retry, and all-or-nothing write semantics.
- Gate: fake-client valid/invalid/unavailable tests pass; no provider response becomes evidence.

### Wave 4: Review and promotion

- Implement decision-file parsing, append-only `onto_review` application, effective latest-decision lookup, stale-review handling, and materialization into semantic `onto_*` records.
- Add `ontology review apply` and `ontology derive-semantics` CLI commands with help/docs.
- Gate: fixture acceptance exports relation/rule/lifecycle; rejection and stale evidence do not.

### Wave 5: XML and real-project verification

- Extend exporter module coverage only for accepted materialized records.
- Run all tests and strict validator.
- Reindex only an isolated copy/fresh database. Generate v1 candidate output and report it.
- If no human decision file is supplied, mark the final real-project promotion subtask `BLOCKED` with the precise required decision-file format; do not claim accepted semantic relations/rules/lifecycles.

## Required Status Discipline

- Update each `track.xml` task only after its stated evidence exists.
- Write a report after each wave with commands, result, and remaining risk.
- If the source parser cannot produce a required direct semantic claim, do not replace it with name heuristics; record a diagnostic and revise the plan only with evidence.
- Do not mark the parent mission complete until either real promotion is explicitly accepted or the parent is re-planned to separate the human-gated promotion deliverable.

## Final Evidence Checklist

- All fixture classes named in design section 7 exist and discriminate success/failure.
- `depa_*` sentinel proves isolation across every new path.
- Existing v0 artifact remains unchanged.
- v1 bundle is fresh and strict-valid after an accepted fixture decision.
- Full LlmWiki smoke suite passes.
