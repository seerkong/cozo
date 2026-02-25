# Wave WAVE-P4-01

- Phase: P4
- Task: T4.1 - viz backend schema/governance API
- Status: DONE

Planned work
- Add new endpoints in `cozo-lib-bun-viz/server/src/index.js`:
  - /api/schema/state
  - /api/schema/versions
  - /api/schema/diff
  - /api/schema/apply
  - /api/schema/rollback
  - /api/governance/seed
  - /api/governance/checkAccess
- Add bun tests for happy paths

Notes
- Must not change existing /api/permission/* semantics
- Keep behavior registry safety in mind (avoid cross-request clearRegistry effects)

Done (2026-03-02)
- Implemented endpoints with `withOmRegistryLock` and without calling `om.clearRegistry()` for these routes
- Added Bun tests for schema/governance happy paths
- Verified: `bun test` in `cozo-lib-bun-viz/server`
