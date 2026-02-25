# Wave WAVE-P4-02

- Phase: P4
- Task: T4.2 - top-nav + router
- Status: DONE

Planned work
- Add 2 new top-nav entries in `cozo-lib-bun-viz/frontend/src/App.vue`:
  - /governance
  - /schema
- Add routes in `cozo-lib-bun-viz/frontend/src/router.ts`
- Do not change existing /permission page

Notes
- Keep visual language consistent with existing viz UI

Completion report
- Added new top-nav entries: `/governance` (Governance) and `/schema` (Schema)
- Added new router entries pointing to placeholder pages
- Added minimal placeholder pages under `cozo-lib-bun-viz/frontend/src/pages/`
- Verified `bunx tsc --noEmit` and `bun run build` in `cozo-lib-bun-viz/frontend/`
