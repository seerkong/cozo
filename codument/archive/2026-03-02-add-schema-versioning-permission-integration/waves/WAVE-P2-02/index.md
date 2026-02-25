# Wave WAVE-P2-02

- Phase: P2
- Task: T2.2 - schema snapshot + diff
- Status: DONE

Planned work
- Implement schema snapshot read/write APIs (for rollback + viz)
- Ensure snapshot covers: type/attr/rel/hierarchy/mixins/alias and perm policy metadata (if relations exist)
- Implement `diffSchemaVersions` based on snapshots
- Add Bun tests

Notes
- commit_mode=manual: do not create git commits

Done
- Added `readSchemaSnapshot` / `writeSchemaSnapshot` / `diffSchemaVersions` in `cozo-lib-bun/cozo-om.js`
- Snapshot payload includes schema + alias sections and a perm section (graceful when om_perm_* relations do not exist)
- Added Bun tests: `cozo-lib-bun/__tests__/om-schema-snapshot-diff.test.js`
