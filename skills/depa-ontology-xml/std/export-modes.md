# DEPA Export Modes

| Format | Reads | Writes | Intended use | Truth status |
| --- | --- | --- | --- | --- |
| `runtime-snapshot` | `depa_*`, OM schema, `ck_meta` | `depa-runtime-snapshot.xml` | Audit, diff, reproducibility | Derived operational record |
| `ontology-xml` | The same rows plus optional fresh report | `ontology.xml`, base modules, evidence, DEPA judgment sidecar | Structured review and C# OM/RDF/diagram projection | Derived semantic projection |
| `wiki` | The same rows plus optional fresh report | Markdown path tree and `_index/manifest.json` | Human/AI navigation | Derived reading projection |

`depa-wiki scan` is separate from all three modes. It is the explicit mutation boundary: it initializes the DEPA ontology and materializes annotations, structural links, effect APIs, violations, and current report verdicts.

Without `--scan-first`, export performs no scan and exposes only persistent DEPA entities, properties, edges, and code anchors. The result must say that a complete `PASS`/`BLOCKED` report is unavailable.

For one source revision, keep the three export directories as siblings. Do not mix generated output with manually edited ontology source.
