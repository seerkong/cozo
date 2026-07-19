---
name: code-to-ontology-xml
description: Read revision-pinned application source code and author evidence-backed business ontology XML. Use when discovering or refreshing domain types, relations, rules, lifecycles, implementation mappings, or cross-repository identities from real code, with optional deterministic inventory and ck_* observations for traceability.
---

# Code To Ontology XML

Author a reviewable business ontology by understanding the selected source code. The model is the semantic recognizer; deterministic inventories and `ck_*` facts are observation accelerators only.

## Route

1. Read [references/workflow.md](references/workflow.md) before choosing scope or interpreting code.
2. Read [references/coverage.md](references/coverage.md) before projecting XML or declaring completion.
3. Read `../ontology-xml-dsl/SKILL.md` and every specification it routes to for the resources being emitted.
4. Treat `ontology-xml-dsl` as the sole grammar owner. Do not copy its XML element, attribute, identity, ordering, or reference rules into this skill.

## Execute

1. **Scope**: Pin repositories and revisions, define bounded scope, exclusions, expected business areas, and a source read plan.
2. **Read**: Read real source in layers, then trace representative calls and end-to-end business flows across repositories.
3. **Observe**: Record source-anchored observations. Use deterministic inventory or `ck_*` only to accelerate navigation, find omissions, and preserve traceability.
4. **Interpret**: Maintain a candidate ledger for types, relations, rules, lifecycles, and mappings. The model proposes semantics from source understanding; tools never create accepted semantics.
5. **Reconcile**: Merge cross-repository identities only after reading both sides and proving the identity link through contracts, identifiers, storage, mappings, or traced flows.
6. **Project**: Emit a modular ontology bundle through the grammar and ownership rules of `ontology-xml-dsl`. Stage it outside existing outputs and, only after its source evidence and validation gates pass, publish it to the caller-selected `cozo-ontology/<version>/ontology/<ontology-id>/` location.
7. **Validate**: Validate every code fact packet, then run the single generated ontology audit entrypoint. It always runs strict generated XML validation first and adds deterministic coverage validation when inventory and manifest sidecars are supplied.

## Non-Negotiable Boundaries

- Source reading, call-chain analysis, and business-process analysis are mandatory. Never replace them with inventory counts, naming heuristics, graph topology, `ck_*`, or a model summary produced without opening the code.
- `depa_*` tables, judgments, sidecars, exports, and ontologies are excluded from every input, interpretation, evidence path, and output.
- Current application ontology outputs are not examples, fixtures, expected results, or oracles.
- Never read `cozo-ontology/baseline/ontology/`, an existing `v0/ontology/` bundle, historical candidate ledgers, coverage sidecars, or DEPA exports while interpreting source semantics. They may be replaced only after a fresh source-only staging bundle passes all gates.
- Do not add gold samples, counterexamples, or domain fixtures while running this skill.
- Every new interpretation defaults to `hypothesis`. It becomes `accepted` only under an explicit review decision or predeclared acceptance policy backed by the required evidence.
- Evidence grade and interpretation confidence are independent. Frontend presentation cannot establish backend enforcement or persisted authority.
- Keep repository-relative evidence paths and immutable revisions. Never persist local absolute roots in portable output.
- Stop with a diagnostic when revision identity, source access, required code paths, evidence anchors, strict generated validation, or semantic coverage cannot be established.
