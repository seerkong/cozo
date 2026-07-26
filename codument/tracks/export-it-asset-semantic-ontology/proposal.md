# Proposal: Export the IT Asset semantic ontology

## Goal

Deliver a complete, evidence-traceable IT Asset business semantic ontology from the two existing project databases. The deliverable is an external artifact, not an `onto_*` promotion and not a count or lightly renamed projection of `ck_semantic_claim` rows.

## Scope

- Extend the repository's read-only investigation CLI so complete indexed semantic evidence can be consumed deterministically through bounded pages.
- Keep prompt text, model invocation, domain synthesis, critic, and final artifacts under the external IT Asset solution directory.
- Copy and reindex only disposable database copies. Never mutate either source database.
- Produce concepts, properties, relationships, business rules, lifecycles, evidence index, domain coverage, provenance, and an explicit unresolved-gap report.
- Materialize the evidence-backed subset as a separately installable Node.js package under the external solution's `packages/`, using a local `file:` dependency on this repository's `cozo-lib-bun` rather than an npm-registry copy.

## Non-goals

- Do not promote or rewrite accepted `onto_*` generations.
- Do not present static claims, symbols, or topology nodes as business concepts.
- Do not require a user review checkpoint before the external final ontology is generated.

## Success

The final artifact explains the operational domains of IT Asset management in business language and lets each normative statement be traced to indexed evidence. Its coverage and uncertainty are stated plainly.

The executable package initializes the same retained semantic subset in a Cozo OM schema. It is a schema installer and validation boundary, not a new source of business evidence and not an `onto_*` writer.
