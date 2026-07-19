# Design: Bounded semantic experiment profiles

## 1. Experiment contract

The CLI keeps `--mode deterministic|assisted` as the transport choice and adds `--experiment v2|v3` for assisted runs. Omitting `--experiment` preserves the existing single-pack assisted behavior for compatibility.

| Profile | Proposal phases | Default maximum | Semantic role |
|---|---:|---:|---|
| `v2` | one proposal per ranked slice | 8 slices / 8 completions | Suggest relations, rules, lifecycles from bounded evidence. |
| `v3` | same proposal + one critic per slice | 8 slices / 16 completions | Retain/drop valid v2-shaped proposals; never create them. |

Both profiles use the same deterministic ranking and same selected slice IDs when executed from the same source fingerprint. CLI may lower `--max-slices` but may never exceed the profile maximum. Byte, candidate and retry limits compose with existing `OntologySemanticAssistedProposalClient` bounds.

## 2. Input, budgeting, and cache

Each slice pack includes only the selected use-case slice, directly supporting semantic claims, canonical concept IDs, source anchors, and necessary corroborations. The orchestration records and checks before every call:

- selected slice count and maximum completions;
- pack UTF-8 bytes and existing output/candidate limits;
- profile/model/prompt-contract/pack digest cache key;
- total calls, cache hits, retries, proposed, retained and dropped counts.

The cache stores canonical locally validated candidate payloads or critic decisions, not model response text, chain-of-thought, stderr, provider diagnostics, or evidence claims. A failed/invalid model response is never cacheable. Any phase failure aborts replacement of the target generation.

## 3. v2

For each ranked slice, call the existing strict proposal client with a slice-specific pack. Merge deduplicated validated candidates by stable candidate ID. Store them as `pending` with their existing direct source evidence. Model rationale is bounded metadata only and is not elevated to `BusinessOntologyEvidence`.

## 4. v3 critic

v3 first executes the identical v2 proposal phase. Its critic receives the same pack plus only the locally validated candidate IDs/payloads for that slice. It returns a closed JSON envelope containing decisions `{candidateId, decision: keep|drop, rationale}`. The local critic validator rejects unknown IDs, duplicate IDs, missing decisions, oversized output, or any candidate/evidence creation. Only `keep` candidates proceed to the pending generation; `drop` is reported in the manifest.

## 5. Artifacts and execution

Each run exports to a caller-supplied empty root such as:

```text
cozo-ontology/v2/ontology/it-asset-management/
cozo-ontology/v3/ontology/it-asset-management/
```

The root contains normal XML/quality/candidate outputs plus `generation/experiment.json`. The manifest includes experiment profile, source fingerprint, generation ID, selected slice digests, effective model, prompt-contract hashes, hard limits, observed counters, cache details, and whether all candidates remain pending. It intentionally excludes raw model response text.

## 6. Verification and manual smoke

All tests use fake clients/executables. Real runs use fresh copied backend/frontend databases and explicit new generation IDs. The manual commands set `DEPA_WIKI_LLM_PROVIDER=codex-cli`; omission of `DEPA_WIKI_LLM_MODEL` uses `gpt-5.6-terra`. The track must record actual call counts and stop before the configured ceiling. No decision file is applied during the experiment run.
