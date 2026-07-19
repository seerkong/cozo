---
name: code-knowledge-wiki-export
description: Export, review, or validate a Markdown Wiki folder from a selected Cozo CodeKnowledge/DEPA snapshot. Use for depa-wiki export --format wiki, generated INDEX.md trees, code evidence pages, architecture pages, and blocked-finding pages.
---

# Code Knowledge Wiki Export

Generate a Wiki as a rebuildable reading tree. It is neither the database source nor a second editable ontology source.

## Route

1. Confirm the selected database, indexed revision, and whether an explicit DEPA scan is required.
2. Read `../depa-ontology-xml/SKILL.md` when DEPA judgment or evidence semantics matter.
3. Export with `depa-wiki export --format wiki` into an empty directory.
4. Run [scripts/validate-wiki-export.ts](scripts/validate-wiki-export.ts).
5. Read from `INDEX.md`; use `_index/manifest.json` for programmatic inventory rather than scraping prose.

## Output Contract

```text
INDEX.md
00-overview/project.md
01-architecture/depa.md
02-code-evidence/evidence.md
03-uncertainty/blocked.md
_index/manifest.json
```

- Overview carries repository identity and CodeKnowledge metadata.
- Architecture groups persisted DEPA entities by type.
- Code evidence only exposes repository-relative `path:line` anchors.
- Uncertainty records diagnostics and `BLOCKED` findings. It must not rewrite them as pass/fail claims.
- The manifest lists every generated page using forward-slash relative paths.

## Command

```bash
depa-wiki export --repo <repository> --db <existing-depa-wiki.db> \
  --format wiki --out <empty-wiki-directory> --scan-first

bun run skills/code-knowledge-wiki-export/scripts/validate-wiki-export.ts \
  <empty-wiki-directory>
```

`--scan-first` is deliberate: omit it for a strictly read-only projection of persisted rows, include it only when the Wiki must contain a fresh conformance report.
