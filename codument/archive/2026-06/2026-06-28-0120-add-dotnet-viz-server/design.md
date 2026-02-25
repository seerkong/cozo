# 方案设计：`cozo-lib-dotnet-viz-server`

## 上下文

现有 Bun viz server 是前端后端契约真源。新 .NET server 的成功标准不是“长得像 Elysia”，而是让现有前端在相同 API 契约下工作，并让可复用库能力沉淀到 `cozo-lib-dotnet/src/` 的合适功能 capsule。

## 方案概览

1. **先补 Om.Core**
   - `BehaviorLogic` 或扩展 `ConstraintLogic`：可执行 action/mutation/interceptor registry + `ExecuteActionAsync`
   - `SchemaLogic.ApplySchemaMigrationAsync` 补齐 Node OM 可观测语义
   - `PermissionLogic` seed input 对齐 Bun governance seed tables

2. **新增基于 Om.Core 的功能 capsule**
   - `cozo-lib-dotnet/src/Om.Batch/`：typed batch input、`IngestBatchAsync`、batch result models、非 workbook-specific 的 value normalization
   - `cozo-lib-dotnet/src/Om.Analytics/`：`ImpactAnalysisAsync`、`OwnershipTreeAsync`、`RiskHotspotAsync` 及 graph/tree/ranking result models
   - 两者依赖 `cozo-lib-dotnet/src/Om.Core/` 的 public facade 或 contracts，不把 HTTP/demo/workbook 逻辑带入库层
   - `ICozoOmStore` 需要暴露 transaction abstraction；`Om.Batch` 与 action/mutation pipeline 必须在事务 store 上执行，失败时 abort，避免留下半批次/半动作写入

2.1. **底层 Cozo transaction 下沉**
   - Bun 侧 rollback parity 来自 wrapper 的 `multiTransact(write)`，不是 OM 层自行补偿
   - .NET 侧要实现同等语义，需要从 `cozo-lib-c` 暴露 begin/run/commit/abort transaction ABI，再由 `CozoDb` 包装为 transaction object
   - C# OM 不直接依赖 C ABI；它只依赖 `ICozoOmStore.BeginTransactionAsync`，保持 DEPA capsule 边界

3. **新建 server 包**
   - 目录建议：`cozo-lib-dotnet-viz-server/`
   - 技术建议：ASP.NET Core minimal API，目标框架与 `cozo-lib-dotnet` 保持一致（当前为 `net10.0`）
   - ProjectReference 到 `cozo-lib-dotnet/Cozo.DotNet.csproj`
   - 不引入 Node/Bun runtime 依赖

4. **API compatibility layer**
   - 路由名、HTTP 方法和 response envelope 与 Bun server 保持一致
   - JSON 使用 camelCase/原字段名混合兼容策略，避免破坏前端现有 TypeScript 类型
   - 错误响应保持 `{ status: "error", error }` for demo run endpoints

5. **Demo catalog and adapters**
   - 将 Bun server demos 的 schema/data/query 意图移植为 C# demo definitions
   - 六个 Bun demo id 必须全部覆盖：`procurement`、`hr`、`crm`、`it-asset`、`approval-flow`、`org-timeline`
   - demo-specific raw CozoScript permission models 留在 server 包
   - workbook parser 只负责 table convention -> typed OM batch / permission seed

6. **Database lifetimes**
   - `/api/run` 和 `/api/permission/run` 使用 per-request fresh in-memory CozoDb
   - `/api/schema/*` 使用 server-scoped shared DB
   - `/api/governance/seed` resets governance DB
   - `/api/governance/integrity/seed-demo` resets integrity DB

## 影响范围与修改点（Impact）

- `cozo-lib-dotnet/src/Om.Core/**`：原 `src/Om/` 重命名后的核心本体 capsule，新增或扩展 action/schema/permission core logic
- `cozo-lib-dotnet/src/Om.Batch/**`：新增 batch ingestion 功能 capsule
- `cozo-lib-dotnet/src/Om.Analytics/**`：新增 graph/tree/ranking analytics 功能 capsule
- `cozo-lib-dotnet-viz-server/`：新增 ASP.NET Core server project、demo catalog、tests

## 决策摘要

- 实现第一步先将原 `cozo-lib-dotnet/src/Om/` 项目/目录重命名为 `cozo-lib-dotnet/src/Om.Core/`
- 原 `cozo-lib-dotnet/src/Om/` 重命名为 `cozo-lib-dotnet/src/Om.Core/`，核心本体能力沉淀到 Om.Core
- `ingestBatch` 沉淀到 `cozo-lib-dotnet/src/Om.Batch/`（或等价功能 capsule），依赖 Om.Core
- `impactAnalysis`、`ownershipTree`、`riskHotspot` 沉淀到 `cozo-lib-dotnet/src/Om.Analytics/`（或等价功能 capsule），依赖 Om.Core
- HTTP/demo/presentation 能力落到 `cozo-lib-dotnet-viz-server`
- 现有 Vue 前端后端 API 契约是 server parity 的验收标准
- Permission model demo 的 raw CozoScript 不沉到 Om.Core
- 事务能力属于底层 Cozo wrapper + OM store 抽象；demo catalog 迁移属于 viz-server，不进入 cozo-lib-dotnet

## 风险 / 权衡

- **Demo catalog 迁移量大**：当前 track 追加任务要求补齐六个 Bun demo 的可运行 catalog，至少覆盖每个 demo 的核心 query。
- **事务 ABI 扩展影响 native runtime**：C ABI 与 .NET P/Invoke 要同步更新，并需要重建/替换本机测试用 native library。
- **C# action delegate pipeline 与 Node 全局 registry 不同**：C# 应利用 runtime-scoped registry，避免 Node 侧需要 lock 的问题。
- **Schema migration apply 语义可能不完整**：需要对照 Node OM tests 和 server schema API tests 补可观测行为。
- **JSON field casing 容易破坏前端**：server DTO 需要显式 JSON property names 或统一 camelCase 配置并对特殊字段保留原名。

## 待解决问题

- 是否让 `cozo-lib-dotnet-viz-server` 同时 serve frontend static assets，还是只提供 API。
- Demo catalog 是 C# 手写静态定义，还是先从 Bun demo 数据导出 JSON fixture 再由 C# 加载。
- 是否在本 track 同时加入 CI wiring，取决于仓库现有 .NET 测试约定。
