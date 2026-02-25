# Wave WAVE-P4-03

- Phase: P4
- Task: T4.3 - /governance page (ontology + permission)
- Status: DONE

Planned work
- Build UI to seed demo and run `checkAccess`
- Call server endpoints:
  - POST /api/governance/seed
  - POST /api/governance/checkAccess (or /api/governance/explain)
- Render allow/deny, matchedPolicies, witness path, ABAC comparisons, fieldVisibility

Notes
- Must not touch legacy `/permission` demo

Done (2026-03-02)
- Implemented usable /governance UI with Seed + Run controls
- Wired to server endpoints: `/api/governance/seed` and `/api/governance/explain`
- Renders allow/deny, matchedPolicies, witness path, explanation JSON, and fieldVisibility
