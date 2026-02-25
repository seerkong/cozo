# Bottom-Layer Split Guidance

## Put In `cozo-lib-dotnet/src/Om.Core`

These are core ontology/governance capabilities:

- Executable behavior pipeline:
  - register/define mutation delegates
  - register/define action delegates
  - before/after interceptors
  - `ExecuteActionAsync`
  - parent action call support if needed by approval-flow parity
- Full schema migration apply semantics matching Node OM enough for `/api/schema/apply`.
- Permission seed input compatible with governance seed tables and `CheckAccessAsync` output rich enough for `/api/governance/explain`.

## Put In `cozo-lib-dotnet/src/Om.Batch`

This is a reusable functional capsule depending on `Om.Core`, not part of the core `Om.Core` capsule:

- `IngestBatchAsync`: write entities, properties, and edges from typed batch input, optionally validating touched entities.
- Batch input/result models.
- Typed value normalization helpers that are not workbook-specific.

## Put In `cozo-lib-dotnet/src/Om.Analytics`

This is a reusable functional capsule depending on `Om.Core`, not part of the core `Om.Core` capsule:

- Template query models and APIs:
  - `ImpactAnalysisAsync`
  - `OwnershipTreeAsync`
  - `RiskHotspotAsync`
- Template result models for graph/tree/ranking visuals, including stable JSON-friendly shapes.

## Put In `cozo-lib-dotnet-viz-server`

These are server/demo/presentation concerns:

- ASP.NET Core minimal API host.
- Route contract compatibility with Bun viz server.
- Demo catalog and query definitions.
- Workbook/table convention parsing:
  - `类型定义`
  - `属性定义`
  - `关系定义`
  - `实体数据`
  - `属性数据`
  - `边数据`
  - governance permission seed tables
- Frontend response adapters:
  - `visual.graph` -> `{ graph: { nodes, edges } }`
  - `visual.tree` -> `TreeNode[]`
  - `visual.ranking` -> table rows
- Direct CozoScript permission model demos from `server/src/permission/models.js`.
- Separate in-memory database lifecycle for normal demos, schema endpoints, governance seed, and integrity seed.

## Non-Goals For This Track

- Do not rewrite the Vue frontend.
- Do not move server-only demo catalogs into `cozo-lib-dotnet`.
- Do not change existing Bun server behavior unless a parity bug is discovered and explicitly documented.
