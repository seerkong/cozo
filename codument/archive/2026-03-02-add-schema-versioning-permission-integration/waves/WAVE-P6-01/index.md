# Wave WAVE-P6-01

- Phase: P6
- Task: T6.1 - backend seed-template + editable seed
- Status: DONE

Done (2026-03-02)
- Added GET /api/governance/seed-template returning default tables
- Extended POST /api/governance/seed to accept optional body.tables (no-body keeps default behavior)
- Covered by bun tests in `cozo-lib-bun-viz/server/src/schema-governance-api.test.js`
