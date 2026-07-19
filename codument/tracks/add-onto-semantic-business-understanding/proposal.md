# Proposal: Semantic business-ontology understanding and review promotion

## Goal

Extend the independent `onto_*` pipeline from structural code hypotheses to evidence-governed business-semantic candidates: relations, rules, lifecycles, states, and transitions. The result must distinguish source facts, model proposals, and human-approved ontology declarations.

## Why

The current v0 projection is intentionally conservative: Java DTO/entity/VO shapes become `hypothesis` types and fields become `hypothesis` attributes. It does not infer how business objects relate, what conditions govern them, or how they move through states. Adding more naming rules would enlarge the output without increasing semantic reliability.

## Scope

- Persist source-semantic claims derived from Java/Spring syntax and framework annotations.
- Produce deterministic candidate relations, rules, and lifecycle candidates from those claims.
- Add an optional LLM-assisted proposal stage with strict JSON validation and bounded source evidence packs.
- Add review decision application and review-aware promotion to exportable `onto_*` records.
- Add explicit CLI operations, fixture coverage, and a real-project v1 candidate audit/export.

## Non-Goals

- No automatic acceptance of business candidates.
- No use of `depa_*` as an input or proxy for business rules.
- No changes to Bun, `Om.Core`, or the legacy DEPA exporter semantics.
- No implicit reindex or destructive mutation of existing project databases.
- No overwrite of `cozo-ontology/v0`; real-project output belongs under `cozo-ontology/v1`.

## Success Criteria

1. A fresh test database captures parser/framework source claims with direct evidence and no business-level objects in `ck_*`.
2. Deterministic and assisted modes produce validated pending candidates with stable ids and evidence references.
3. A review fixture can promote one relation, one rule, and one lifecycle with state/transition into exportable XML; rejected and stale candidates remain absent.
4. The full LlmWiki smoke suite, strict track validation, and generated DSL validator pass.
5. A read-only real-project audit produces a fresh v1 candidate bundle and records counts by candidate kind; production acceptance is deliberately blocked until a reviewer decision file is supplied.

## 2026-07-18 Semantic Correction

The first dogfood run invalidated the original completion claim. Its 836 concepts were mostly
one-for-one projections of DTO/entity/VO/request carriers, its 492 rules were mostly field
annotations, and it found only two typed-field relations and no lifecycle. Those results are
source-structure inventory, not business-semantic understanding.

The corrected scope therefore also requires:

- canonical business-concept consolidation across transport, persistence, API, service, and
  frontend carriers;
- bounded cross-file use-case slices that join entry actions, service behavior, repository or
  mapper access, domain state, and frontend corroboration;
- conditional business rules with precondition, governed subject, and allow/reject effect;
- lifecycle reconstruction from state reads/guards and state writes, including setter-based and
  code/string-backed states;
- real-project semantic quality gates. XML validity and nonzero candidate counts alone can no
  longer complete this track.

The deleted v1 bundle and isolated dogfood databases are invalid inputs and must never be reused.
