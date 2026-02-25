# Ontology Demo Frontend

Vue 3 + Vite frontend for the CozoDB ontology visualization demo.

## Setup

```bash
cd cozo-lib-bun-viz/frontend
bun install
```

## Development

```bash
bun run dev
```

Opens at http://localhost:4174. The backend Elysia server should be running at http://localhost:4175 (or set `VITE_API_BASE`).

```bash
# Custom backend URL
VITE_API_BASE=http://localhost:9000 bun run dev
```

## Build

```bash
bun run build
bun run preview
```

## Architecture

```
src/
  lib/api.ts              — API types + fetch helpers
  pages/OntologyDemo.vue  — Main page (toolbar, spreadsheet, results)
  components/
    UniverWorkbook.vue     — UniverJS spreadsheet (6 sheets: 类型定义/属性定义/关系定义/实体数据/属性数据/边数据)
    QueryInspector.vue     — Collapsible query meaning + DSL panel
    ResultTable.vue        — @tanstack/vue-table result grid
    TreeView.vue           — Tree visualization
    GraphView.vue          — vis-network graph visualization
    SchemaGraphView.vue    — Object-relation schema graph
    ResultTabs.vue         — Tab switcher (对象关系 / 列表 / 树 / 图)
```

## API Contract

The frontend expects two endpoints from the Elysia backend:

- `GET /api/demos` → `{ demos: Demo[] }` — list of demos with tables and queries
- `POST /api/run` → `RunResponse` — execute a query against edited spreadsheet data

Each demo has separate workbook data (Procurement/HR/CRM are distinct demos). Switching demo swaps the entire workbook content.
