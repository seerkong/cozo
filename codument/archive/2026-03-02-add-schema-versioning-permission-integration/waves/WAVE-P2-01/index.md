# Wave WAVE-P2-01

- Phase: P2
- Task: T2.1 - MigrationSpec + applySchemaMigration
- Status: DONE

Planned work
- Define minimal MigrationSpec shape used by `applySchemaMigration`
- Implement apply pipeline (preflight strict + apply steps + record migration + write snapshot + bump version)
- Store checksum in schema state/version
- Add Bun tests under `cozo-lib-bun/__tests__`

Notes
- commit_mode=manual: do not create git commits

Done
- Implemented `applySchemaMigration` with strict preflight for valueType change
- Writes migration log (`om_schema_migration`), version row (`om_schema_version`), snapshot (`om_schema_snapshot`), and bumps schema state (`om_schema_state`)
- Computes and stores checksum (sha256 of snapshot JSON)
- Added Bun tests: `cozo-lib-bun/__tests__/om-schema-migration-apply.test.js`
