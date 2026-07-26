# Proposal: Business knowledge investigation and executable Cozo OM skills

## Goal

Provide two focused project skills: one extracts an evidence-backed business knowledge ledger from existing databases through the read-only investigation CLI; the other converts reviewed ledger entries into a runnable local-file-dependent `cozo-lib-bun` package.

## Scope

- Add `skills/investigate-business-knowledge-db/` with complete pagination, graph-guided investigation, evidence/interpretation separation, and no-write boundaries.
- Add `skills/model-business-knowledge-cozo-om/` with runnable package, constraints, lifecycle, comments, and in-memory validation guidance.
- Keep prompts, providers, agent loops, candidate review policy, and source-database mutation out of both skills.

## Success

An agent can select the correct skill from a natural request, use only approved interfaces, produce a reviewable handoff between investigation and modeling, and avoid equating code observations with business ontology.
