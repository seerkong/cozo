---
name: depa-ontology-xml
description: Export and review DEPA runtime snapshots or observation-sidecars without confusing them with canonical BO-first ontology XML. Use for depa-wiki scan/export, runtime snapshots, DEPA judgments, and evidence-safe handoff into ontology investigation.
---

# DEPA Export Boundary

DEPA judgments and runtime snapshots are observations, never canonical BusinessObject ontology declarations.

- `scan` may write `depa_*`; run it only when explicitly requested.
- `export` is read-only unless `--scan-first` is explicitly requested and the target is an empty directory.
- Preserve exported code anchors and raw records as candidate `Evidence` material only after a human/semantic process interprets them.
- Do not place `depa_*` IDs, judgments, paths, or snapshots in canonical BO-first XML semantics. Do not use a DEPA export as proof of a domain process or policy.
- When handing material to `ontology-xml-dsl`, retain repository-relative anchors, revisions, direct excerpts, grade, and the distinction between observation and interpretation.
