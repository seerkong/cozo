---
name: investigate-business-knowledge-db
description: Extract reviewable business evidence from an existing depa-wiki/CodeKnowledge database through its read-only investigation CLI for a BO-first ontology XML bundle. Use for domain concepts, associations, processes, policies, events, rules, lifecycles, evidence gaps, and raw investigation material without database mutation.
---

# Investigate Business Knowledge DB

Use `depa-wiki investigate` only when that command is available. It is a read-only, whitelisted interface; never index, scan, run SQL, promote ontology facts, or mutate the selected database.

## Collect

1. Pin database, repository, revision, CLI version, and the bounded business question.
2. Use overview, domains, terms, use cases, state rules, topology, and focused evidence retrieval only to navigate.
3. Exhaust `evidence list --limit 500` by following `nextCursor`; retrieve only the focused evidence packs needed for each conclusion.
4. Preserve the raw request and returned material: query/body, result format, evidence IDs, repository-relative anchors, excerpts, and cursor/run identity.
5. For every modeled BO, run an explicit action-coverage pass over command endpoints, service methods, callbacks, and state-changing write paths. A schema/attribute-only result is incomplete evidence, not evidence that the BO has no Actions, Mutations, Interceptors, or ComputedFunctions.

## Hand off to BO-first XML

Produce a reviewed ledger grouped by `BusinessObject`, `Association`, `BusinessProcess`, `DomainPolicy`, and `EventContract`. Every entry distinguishes direct material, interpretation, confidence, alternatives, and gaps.

For each reusable item, prepare an `Evidence` payload whose direct text is the actual returned JSON/text/XML or bounded source excerpt; metadata belongs on the `Evidence` attributes. Do not reduce evidence to an ID, path, confidence, or one-line summary. Refer to it with `<Evidences><Evidence ref="Fqn" /></Evidences>`.

Graph degree, symbols, claim counts, names, and topology order investigation only; they never prove a business concept or rule. If the CLI is unavailable, retain the saved read-only evidence artifact and record that limitation rather than recreating, reindexing, or silently substituting a different data source.

Before declaring a BO actionless, record the action-coverage queries, inspected candidate paths, and the reason every candidate was rejected. Otherwise publish an action-coverage gap for downstream synthesis.
