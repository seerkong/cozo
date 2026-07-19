# Proposal: Spring semantic derivation

## Goal

Derive bounded, explainable Spring framework facts from Java symbols and source text so controllers, routes, services, repositories, transactions, listeners/handlers, and data roles are queryable in the existing CodeKnowledge graph.

## Representation

- Role and transaction facts use open `ck_edge` kinds with synthetic Spring concept targets and full provenance fields.
- HTTP routes and listener entry points use `ck_entry_point` with deterministic metadata containing path/topic, source site, resolver, confidence, and evidence.
- Shared `CodeConceptFact` rows name the bounded Spring role vocabulary.

## Out Of Scope

- Meta-annotation expansion, runtime bean wiring, classpath reflection, and business-authoritative ontology claims.
- Complete Spring Expression Language evaluation.
