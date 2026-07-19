# Proposal: Refactor the ontology library to be domain-neutral

## Goal

Remove IT-asset assumptions from the reusable Cozo ontology, LlmWiki and visualization surfaces while preserving their structural inference, quality and example coverage.

## Why

Recent dogfooding introduced hard-coded asset workflow categories, `AssetNumber` filters, an `it-asset` default demo, and asset-named fixtures. Those details make a general database/library evaluate unrelated projects against one customer's business process and blur the distinction between reusable behavior and a project fixture.

## Scope

- Replace production workflow keyword gates and field-name exceptions with domain-neutral structural checks.
- Replace the default Bun visualization asset demo with a generic hierarchy/governance demo and update catalog, UI and E2E contracts.
- Rename or rebuild asset-focused tests, fixtures, examples and skill fixtures using neutral business objects while retaining equivalent coverage.
- Remove asset-specific CLI/docs wording that presents a project workflow as a product requirement.

## Non-goals

- Do not remove support for users to model assets, procurement, HR or any other domain.
- Do not alter Cozo core storage/query behavior or the `onto_*` review/promotion boundary.
- Do not rewrite historical Codument archive material; it remains an audit record.

## Acceptance

1. Production quality/report/projector code contains no asset workflow categories or `AssetNumber` exceptions.
2. The default visual demo and public catalog contain no `it-asset` domain contract.
3. Current semantic and OM tests retain their behavioral coverage with neutral fixtures.
4. Repository scans find asset-domain terms only in explicitly preserved historical records, never in active production, tests, skills, docs or demo defaults.
