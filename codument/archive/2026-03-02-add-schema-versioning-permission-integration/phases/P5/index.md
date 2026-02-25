# Phase P5

- Goal: end-to-end tests + coverage + strict validation
- Waves:
  - WAVE-P5-01: Playwright E2E for /governance and /schema + verify /permission still works
  - WAVE-P5-02: coverage + codument validate

Done (2026-03-02)
- `npm run test:e2e` (cozo-lib-bun-viz/) passes
- `bun test --coverage` (cozo-lib-bun/) passes
- `codument validate add-schema-versioning-permission-integration --strict` passes
