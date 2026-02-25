# 变更：新增 .NET Viz Server 并补齐底层 OM 能力

## 背景和动机 (Context And Why)

`cozo-lib-bun-viz` 当前提供了 Vue 前端需要的 Bun/Elysia 后端。用户目标是提供一个基于 `cozo-lib-dotnet` 的 C#/.NET Core 对等后端：`cozo-lib-dotnet-viz-server`，使现有前端可以切换后端而获得同等功能。

分析发现，Bun viz server 中混合了两类内容：

- 可复用的底层 OM 能力，例如 batch ingestion、impact graph、ownership tree、risk hotspot、schema migration、permission check、existential rules。
- 纯 server/demo/presentation 能力，例如 HTTP routes、demo catalog、workbook table conventions、response adapter、raw permission demo CozoScript。

本 track 的目标是先把边界定清，再实现对等 .NET 版本：原 `cozo-lib-dotnet/src/Om/` 核心本体能力重命名并留在 `cozo-lib-dotnet/src/Om.Core/`，batch/analytics 这类基于 Om.Core 的功能能力沉淀为 `cozo-lib-dotnet/src/` 下的独立功能 capsule，server 能力落到新包 `cozo-lib-dotnet-viz-server`。

## "要做"和"不做" (Goals / Non-Goals)

**目标:**
- 在 `cozo-lib-dotnet/src/` 下补齐 Bun viz server 依赖的可复用库能力，但 `ingestBatch`、`impactAnalysis`、`ownershipTree`、`riskHotspot` 不直接进入核心 `src/Om.Core/`。
- 新建 `cozo-lib-dotnet-viz-server` ASP.NET Core server 包。
- 兼容现有 `cozo-lib-bun-viz/frontend` 使用的后端 API 与 JSON response envelope。
- 保留 DEPA 边界：底层 OM 不包含 HTTP/demo 展示逻辑，server 不私造可复用 OM 语义。
- 增加 .NET server/API tests，尽量复用 Bun server API test 的行为断言。

**非目标:**
- 不重写 Vue 前端。
- 不把 demo catalog、workbook 中文表名、HTTP response adapter 沉入 `cozo-lib-dotnet`。
- 不改变 Bun server 的现有契约，除非发现明确 bug 且另行记录。
- 不要求第一次实现支持生产级持久化配置；先保持与 Bun server 一致的内存 demo 语义。

## 变更内容（What Changes）

- `cozo-lib-dotnet/src/Om.Core/` 作为原 `src/Om/` 的重命名核心本体 capsule，增加或补齐：
  - executable action/mutation/interceptor delegate pipeline；
  - schema migration apply parity；
  - governance permission seed compatibility improvements。
- `cozo-lib-dotnet/src/Om.Batch/`（或等价功能 capsule）增加 batch ingestion API，依赖 Om.Core。
- `cozo-lib-dotnet/src/Om.Analytics/`（或等价功能 capsule）增加 template query API：impact analysis、ownership tree、risk hotspot，依赖 Om.Core。
- 新增 `cozo-lib-dotnet-viz-server/`：
  - ASP.NET Core minimal API host；
  - `/api/demos`、`/api/run`；
  - `/api/permission/models`、`/api/permission/run`；
  - `/api/schema/*`；
  - `/api/governance/*` 与 `/api/governance/integrity/*`；
  - demo catalog and workbook parser；
  - frontend response adapters。
- 新增测试：
  - OM bottom-layer parity tests；
  - server endpoint contract tests；
  - optional frontend smoke compatibility against existing `cozo-lib-bun-viz/frontend` if implementation chooses to run the Vue app.

## 影响范围（Impact）

- 受影响的能力（behaviors）：`cozo-dotnet-viz-server`
- 受影响的代码：
  - `cozo-lib-dotnet/src/Om.Core/**`
  - `cozo-lib-dotnet/tests/**`
  - `cozo-lib-dotnet-viz-server/**`（新增）
  - 可能新增 solution/test project wiring
  - `cozo-lib-dotnet/README.md` 或新增 server README
