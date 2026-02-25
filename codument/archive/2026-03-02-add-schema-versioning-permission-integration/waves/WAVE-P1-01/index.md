# Wave WAVE-P1-01

- Phase: P1
- Task: T1.1 - schema versioning metadata relations
- Status: DONE

Planned work
- Add schema versioning stored relations in `cozo-lib-bun/cozo-om.js:initSchema`
- Seed initial schema state (current_version=1) without overwriting existing DB state
- Add `getSchemaState` and `listSchemaVersions` APIs + typings
- Add Bun tests under `cozo-lib-bun/__tests__`

Notes
- commit_mode=manual: do not create git commits

Done
- Added schema versioning + alias metadata stored relations in `cozo-lib-bun/cozo-om.js:initSchema`
- Seeded default schema state and v1 version rows (idempotent)
- Added `getSchemaState` and `listSchemaVersions` APIs + typings
- Added Bun tests: `cozo-lib-bun/__tests__/om-schema-versioning-metadata.test.js`
