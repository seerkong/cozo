# Investigation CLI reference

Use a copied or existing database with an explicit `--db "$DB"`. The routes below are read-only.

```bash
depa-wiki investigate overview get --db "$DB"
depa-wiki investigate domains discover --term asset --limit 10 --db "$DB"
depa-wiki investigate topology domain --term asset --db "$DB"
depa-wiki investigate evidence list --limit 500 --db "$DB"

depa-wiki investigate evidence get --db "$DB" --json - <<'JSON'
{
  "evidenceIds": ["semantic:<id-1>", "semantic:<id-2>"]
}
JSON
```

Available route families include `terms find`, `patterns find`, `evidence list|get`, `domains discover`, `use-cases list`, `state-rules find`, `implementations find`, `topology domain`, `ontology subjects inspect`, and `ontology use-cases list|get`.

`evidence list` is keyset-paginated. Request up to 500 rows, retain the signed `nextCursor`, and continue until `nextCursor` is `null`. Cursors must be passed unchanged to the same route/database; do not synthesize one.

`--json -` is limited to a 1 MiB JSON object. It is appropriate for multiline bodies, not prompt injection or arbitrary query input. The CLI rejects SQL, arbitrary source paths, prompts, provider configuration, and mutations.
