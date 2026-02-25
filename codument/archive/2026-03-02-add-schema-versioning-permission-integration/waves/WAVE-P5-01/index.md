# Wave WAVE-P5-01

- Phase: P5
- Task: T5.1 - Playwright E2E
- Status: DONE

Planned work
- Add Playwright tests for:
  - /governance (seed + run + allow/deny shown)
  - /schema (state + versions + apply + diff + rollback)
- Ensure legacy /permission smoke still passes
- Run `npm run test:e2e` under `cozo-lib-bun-viz/`

Notes
- Keep Playwright test file names as `*.pw.ts` (avoid bun test picking them up)

Done (2026-03-02)
- Added `cozo-lib-bun-viz/e2e/governance-schema.pw.ts`
- Confirmed legacy `/permission` smoke remains covered via `cozo-lib-bun-viz/e2e/smoke.pw.ts`
- Verified: `npm run test:e2e` under `cozo-lib-bun-viz/`
