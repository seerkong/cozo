# Design: External semantic ontology delivery

## Boundary

`cozo` supplies only a stable, read-only evidence data plane. It exposes paginated semantic evidence alongside the existing domain, state/rule, use-case, implementation-cluster, topology, and excerpt operations. It does not embed prompt text, provider configuration, agent loops, or ontology publication policy.

`/Users/kongweixian/ai/solution/it-asset-ai-solution/cozo-ontology/v4/` owns the model-facing work: evidence collection, domain prompts, synthesis schema, critic, merge, and deliverable. The runner copies the two source databases to a temporary directory before rebuilding CodeKnowledge facts, then invokes only `depa-wiki investigate` against those copies.

## Evidence Flow

1. Fetch overview and enumerate all `evidence/list` pages from each copied database.
2. Group evidence into operational domains, then request targeted topology, rule, use-case, and source-excerpt packs.
3. Ask the model to return only structured concepts, relations, rules, and lifecycles with evidence ids; reject implementation-name-only output.
4. Run a separate critic pass, merge only retained statements, and write the final XML/JSON plus coverage, provenance, and gaps.

## Completeness Contract

An evidence list page has a signed continuation cursor, a maximum page size, and deterministic claim-id ordering. Internally, the service reads the underlying relation in keyset pages, so no fixed 5,000-row truncation can silently turn a 22,163-row corpus into an apparent complete input.

The resulting ontology is complete relative to the observable implementation evidence and named domains, not a claim of complete stakeholder intent. The deliverable explicitly records ambiguity and missing evidence.

## Safety

The runner fails before execution if a source DB path equals a write target. It creates copies in a temporary directory, and it never invokes ontology mutation or publication commands.

## Executable Cozo OM Projection

`/Users/kongweixian/ai/solution/it-asset-ai-solution/packages/it-asset-management-ontology/` is an ESM Bun/Node.js workspace package. Its manifest uses `cozo-lib-bun: file:/Users/kongweixian/infra-dev/cozodb/cozo/cozo-lib-bun`; it does not resolve the dependency from a package registry.

`installItAssetManagementOntology(runner)` calls the public `cozo-om` APIs to define the retained 14 business types and 7 directed relations. The 10 evidence-backed rules become `cozo-om` constraints, while two additional constraints make the acceptance and return-reinbound lifecycle states executable. The package itself writes no business instances: callers own instance creation and can use ordinary `om.createEntity`, `om.setProperty`, and `om.linkEntities` APIs.
