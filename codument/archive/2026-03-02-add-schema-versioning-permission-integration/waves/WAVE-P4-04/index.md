# Wave WAVE-P4-04

- Phase: P4
- Task: T4.4 - /schema page (schema versioning)
- Status: DONE

Planned work
- Build UI to show current schema state + versions
- Provide diff UI (from/to version)
- Provide apply migration + rollback controls and display diagnostics

Notes
- Must not touch legacy `/permission` demo

Done (2026-03-02)
- Implemented /schema UI: state, versions, diff, apply migration, rollback (strict toggle)
- Uses the new `/api/schema/*` endpoints and shows responses as JSON
