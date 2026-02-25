# cozo-viz server

Elysia backend for cozo-lib-bun-viz. Provides APIs for the frontend to list demos and execute OM/DSL queries against in-memory CozoDB instances.

## Prerequisites

- [Bun](https://bun.sh) >= 1.0
- `cozo-lib-bun` native module built (at `../../cozo-lib-bun`)

## Setup

```bash
cd cozo-lib-bun-viz/server
bun install
```

## Run

```bash
# Development (auto-reload)
bun run dev

# Production
bun run start
```

Server listens on **http://localhost:4175**.

## API

### `GET /api/demos`

Returns all available demos with their default workbook tables and query definitions.

```json
{
  "demos": [
    {
      "id": "procurement",
      "label": "采购管理",
      "tables": [
        { "name": "类型定义", "columns": ["typeName", "description"], "rows": [/* ... */] },
        { "name": "属性定义", "columns": ["typeName", "attrName", "valueType", "required"], "rows": [/* ... */] },
        { "name": "关系定义", "columns": ["relName", "fromType", "toType", "directed"], "rows": [/* ... */] },
        { "name": "实体数据", "columns": ["id", "typeName", "label"], "rows": [/* ... */] },
        { "name": "属性数据", "columns": ["entityId", "attrName", "value"], "rows": [/* ... */] },
        { "name": "边数据", "columns": ["fromId", "relName", "toId", "props"], "rows": [/* ... */] }
      ],
      "queries": [
        {
          "id": "dslQuery",
          "label": "高价值行项目",
          "meaning": "...",
          "dsl": "...",
          "defaultView": "table"
        }
      ]
    }
  ]
}
```

### `POST /api/run`

Execute a query. Body:

```json
{
  "demoId": "procurement",
  "queryId": "impactAnalysis",
  "tables": [
    { "name": "类型定义", "columns": ["typeName", "description"], "rows": [/* ... */] },
    { "name": "属性定义", "columns": ["typeName", "attrName", "valueType", "required"], "rows": [/* ... */] },
    { "name": "关系定义", "columns": ["relName", "fromType", "toType", "directed"], "rows": [/* ... */] },
    { "name": "实体数据", "columns": ["id", "typeName", "label"], "rows": [/* ... */] },
    { "name": "属性数据", "columns": ["entityId", "attrName", "value"], "rows": [/* ... */] },
    { "name": "边数据", "columns": ["fromId", "relName", "toId", "props"], "rows": [/* ... */] }
  ]
}
```

`tables` is optional — defaults to the demo's seed data if omitted.

Response:

```json
{ "status": "ok", "table": { "columns": ["..."], "rows": [/*...*/] } }
```

Or:

```json
{ "status": "ok", "tree": [ { "id": "...", "label": "...", "children": [] } ] }
```

Or:

```json
{ "status": "ok", "graph": { "nodes": [/*...*/], "edges": [/*...*/] } }
```

On error:

```json
{ "status": "error", "error": "..." }
```
