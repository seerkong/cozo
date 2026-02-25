# Phase P6

- Goal: governance sub-tabs + editable seed via UniverWorkbook
- Waves:
  - WAVE-P6-01: backend seed-template + custom seed
  - WAVE-P6-02: frontend tabs + data-prep workbook
  - WAVE-P6-03: e2e regression for tabs

Done (2026-03-02)
- Backend: GET /api/governance/seed-template + POST /api/governance/seed supports body.tables
- Frontend: /governance has "数据准备" and "权限查询" tabs; seed template is editable and can be applied
- Tests: bun tests + Playwright e2e green
