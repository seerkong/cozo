# Wave WAVE-P3-01

- Phase: P3
- Task: T3.1 - permission metadata relations + seed
- Status: DONE

Planned work
- Extend `cozo-lib-bun/cozo-om.js:initSchema` to create stored relations:
  - om_perm_action
  - om_perm_policy
  - om_perm_abac_rule
  - om_perm_path_rule
- Add Bun tests under `cozo-lib-bun/__tests__`

Notes
- commit_mode=manual: do not create git commits

Done
- `initSchema` creates om_perm_* stored relations
- Added `seedPermissionMetadata` helper to upsert permission metadata rows
- Added Bun tests: `cozo-lib-bun/__tests__/om-perm-schema.test.js`
