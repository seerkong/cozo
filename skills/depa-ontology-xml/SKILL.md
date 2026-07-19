---
name: depa-ontology-xml
description: Export, review, or validate DEPA runtime snapshots and normalized ontology XML bundles from an existing Cozo CodeKnowledge/DEPA database. Use for depa-wiki scan/export, runtime-snapshot, ontology-xml, DEPA judgment sidecars, and evidence-safe DEPA exports.
---

# DEPA Ontology XML Export

This skill extends `ontology-xml-dsl`; it does not replace it. XML ontology modules remain a derived semantic projection, while `depa_*` rows remain runtime judgments and `ck_*` rows remain code observations.

## Route

1. Read `../ontology-xml-dsl/SKILL.md` and its evidence, naming, bundle, type/relation, and mapping specifications before changing ontology XML.
2. Read [std/export-modes.md](std/export-modes.md) to choose the required artifact and mutability boundary.
3. Read [spec/depa-judgment-resource.md](spec/depa-judgment-resource.md) before consuming or changing `judgments/depa-judgments.xml`.
4. Run an explicit DEPA scan when a current `PASS` / `GAP` / `BLOCKED` report is required.
5. Export into a new or empty output directory, validate the base ontology root and the DEPA sidecar, then retain the CLI result JSON beside the run record.

## Commands

```bash
depa-wiki scan --repo <repository> --db <existing-depa-wiki.db>

depa-wiki export --repo <repository> --db <existing-depa-wiki.db> \
  --format runtime-snapshot --out <empty-output-directory>

depa-wiki export --repo <repository> --db <existing-depa-wiki.db> \
  --format ontology-xml --out <empty-output-directory> --scan-first

bun run skills/ontology-xml-dsl/scripts/validate-ontology-xml.ts \
  <output-directory>/ontology.xml --workspace-root <output-directory>

bun run skills/depa-ontology-xml/scripts/validate-depa-export.ts \
  <output-directory>
```

## Non-negotiable Boundaries

- `scan` is explicit and may materialize/update `depa_*`; `export` is read-only unless `--scan-first` is explicitly supplied.
- Never point an export at a non-empty directory. Do not treat an exported XML bundle as an importable replacement for the database.
- `runtime-snapshot` is a faithful operational snapshot. It may contain raw row IDs and JSON values.
- `ontology-xml` is a normalized projection. `ontology.xml` contains only base DSL modules; `judgments/depa-judgments.xml` is a DEPA extension sidecar and must not be passed to the base validator as a module.
- A missing fresh report means the exporter must say that `PASS` and `BLOCKED` are unavailable. Persistent `depa_violation` rows alone do not prove a rule passed.
- Evidence paths must be repository-relative; absolute local paths are forbidden in normalized XML.
