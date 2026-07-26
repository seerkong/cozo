# v3 pending publication contract

Use only the public tool calls below. Both accept just `domainTerms`, a literal string or one-to-six literal strings.

```bash
depa-wiki call run_business_semantic_synthesis \
  --db "$COPIED_DB" --work-dir "$PROJECT" \
  --arguments-json '{"domainTerms":["asset","acceptance"]}'
```

To publish a review bundle, configure the server process before invocation:

```bash
export DEPA_WIKI_SEMANTIC_ARTIFACT_ROOT="$ARTIFACT_ROOT"
export DEPA_WIKI_SEMANTIC_BASELINE_ONTOLOGY_ID="ItAssetManagement.Ontology"
depa-wiki call publish_business_semantic_synthesis \
  --db "$COPIED_DB" --work-dir "$PROJECT" \
  --arguments-json '{"domainTerms":["asset","acceptance"]}'
```

`DEPA_WIKI_SEMANTIC_ARTIFACT_ROOT` is mandatory and must be controlled by the server. `DEPA_WIKI_SEMANTIC_BASELINE_ONTOLOGY_ID` is optional: without a readable baseline, v3 still writes a pending bundle but reports `semantic_quality_failed` with `verified_baseline_provider_unavailable`.

The run directory is `semantic-v3-<input-digest>-<critic-digest>` and contains exactly:

- `domain-charters.json`
- `semantic-candidate.xml`
- `review-packet.json`
- `quality-report.json`
- `provenance.json`

The caller must not supply an artifact path, ontology ID, SQL, prompt, provider configuration, evidence identifiers, or review decision. The tool never writes accepted ontology records. A quality pass only says the pending bundle passed the local structural, provenance, evidence-closure, and projection-difference gates; it does not accept the ontology.
