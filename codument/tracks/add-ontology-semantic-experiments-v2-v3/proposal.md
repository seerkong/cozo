# Proposal: Versioned v2/v3 business-semantic experiments

## Goal

Run two controlled, comparable Codex-assisted semantic-understanding experiments over the already indexed business evidence:

- **v2**: one evidence-bound proposal pass per ranked business use-case slice;
- **v3**: the identical proposal pass plus a constrained critic pass that only retains or drops locally validated proposals.

The experiments must produce independent, auditable artifacts under `cozo-ontology/v2` and `cozo-ontology/v3`, without treating model output as business truth.

## Why

The current v1 generation establishes a deterministic candidate baseline. Its semantic candidates are evidence-bound but pending-only. A single global LLM request is neither a useful cost control unit nor a fair experiment unit. Use-case slices allow the system to compare model strategies against the same inputs, cap consumption, and retain a reviewer-readable provenance trail.

## Scope

- Add explicit experiment profile and bounded CLI options to `ontology derive-semantics`.
- Add ranked use-case slice selection, budget enforcement, canonical cache keys, and experiment manifest/quality metrics.
- Implement v2 proposal orchestration and v3 critic-only pruning with strict JSON contracts.
- Make artifact version roots explicit and generate v2/v3 independently from fresh copied databases.
- Add fake-client tests, deterministic cache/budget tests, and a manual Codex smoke procedure using `gpt-5.6-terra`.

## Non-Goals

- No model access to repository cwd, database files, unbounded source trees, or arbitrary tools.
- No automatic candidate acceptance, review decision generation, materialization, or cross-version review inheritance.
- No use of `depa_*` as business ontology input.
- No attempt to optimize pricing or infer exact provider token billing from undocumented CLI output.

## Success Criteria

1. v2 and v3 are selected explicitly, enforce fixed upper bounds before each completion, and emit comparable manifests.
2. v3 cannot introduce a candidate that v2/local validation did not already produce.
3. Both experiments write separate v2/v3 artifacts and leave v1 and original project databases untouched.
4. Test fixtures prove cache behavior, budget stop, critic rejection paths, failure atomicity, and no automatic promotion.
5. A bounded manual run can create pending-only v2/v3 candidate bundles using the local Codex CLI default model.
