# Mission Design：add-dotnet-llm-wiki-viz

## 控制论模型

- desired state：`mission.xml` 中的 TaskGroup DAG、候选 TrackLink、节点状态，以及本文件定义的边界约束。
- actual state：`cozo-lib-dotnet-llm-wiki` 当前 packages、`cozo-lib-bun-viz` 前端/server 参考、active/archived tracks、构建测试结果、用户新增约束。
- actuation：创建/执行/修订/归档 tracks，或在 evidence 变化时更新 mission.xml 与 reports。
- feedback / drift：编译失败、API shape 不适合前端、前端 E2E 失败、现有 CLI/MCP 逻辑无法复用、用户改变范围。

## Mission Actors

| Actor | 控制论角色 | DEPA 归属 | 职责 |
|---|---|---|---|
| MissionPlanner | 期望态产出者 | Processor + Actor | 将 viz/server 目标切成可落地 tracks，维护 DAG、验收和 TrackLink。 |
| MissionObserver | 传感器 | Data + Actor | 读取当前代码、Codument tracks/archive、构建测试结果、前端运行证据和用户约束。 |
| MissionReconciler | 控制器 | Processor + Actor | 比较 desired vs actual，判断 ready/drift/blocked/done，并决定是否需要重规划。 |
| MissionApplier | 执行器 | Effect + Actor | 执行一个 bounded convergence action：创建 track、实现 track、运行验证、更新 mission/reports。 |

## DEPA 边界

- Data：repo DB、tool metadata、HTTP DTO、前端 view state、graph nodes/edges。
- Effect：CozoDB 读写、filesystem scanning、wiki 文件写入、HTTP server、frontend fetch、CLI stdout/stderr。
- Processor：tool argument binding、search/query planning、symbol/impact to graph projection、wiki result mapping。
- Actor：MCP server、CLI command handler、HTTP server host、Vue app interaction loop、mission actors。

## Plan vs Track 区分

Mission 只管理路线，不直接改实现。建议切片：

1. `extract-llm-wiki-tool-runner`：抽出 shared tool runner/contracts/storage options，保持 MCP/CLI 行为不变。
2. `add-cozo-wiki-serve-api`：新增 `Cozo.DotNet.LlmWiki.Server`，实现 ASP.NET Core minimal API、`cozo-wiki --serve`、static-dir/api-only 支持。
3. `add-dotnet-llm-wiki-viz-frontend`：新建 Vue/Vite 前端工作台，接入 server API，提供搜索、graph、wiki、query views。
4. `verify-llm-wiki-viz-e2e`：补 README/help、server smoke、frontend build、可行时 Playwright E2E。

## API Contract 初稿

- `GET /health`
- `GET /api/status`
- `GET /api/tools`
- `POST /api/tools/call` with `{ name, arguments }`
- `POST /api/index`
- `POST /api/embeddings/index`
- `POST /api/search/semantic`
- `POST /api/symbol/context`
- `POST /api/symbol/impact`
- `POST /api/wiki/build`
- `POST /api/query/named`

MVP 不开放 raw CozoScript endpoint。需要更高阶 Datalog 查询时优先通过 `query_named` 和后续 NamedQuery registry 扩展。

## 前端工作台初稿

首屏直接是工具界面：

- 左侧：work-dir/db/status、index 与 embedding actions。
- 顶部：搜索框。
- 主区 tabs：Search、Graph、Wiki、Symbol/Impact、Named Query、Tools。
- 图视图使用 `vis-network` 风格节点/边；从 `symbol_context` 和 `impact_of_change` 的结果投影得到 graph model。
- Result/Query 组件参考 `cozo-lib-bun-viz/frontend`，但文案和数据结构面向 repo knowledge。

## 受控重规划

active mission 允许改动 `mission.xml`，但必须满足：

- 有 evidence 或 human decision。
- 写入 `reports/replan-XXX.md` 或 `reports/human-intervention-XXX.md`。
- 递增 `Metadata.Revision` 并更新 `UpdatedAt`。
- 明确 trigger、actual state、desired state、diff、decision、applied change。

## 风险

- Tool runner 当前是 `McpServer/Program.cs` 内部类型，抽出时容易破坏 MCP stdout 纯 JSON 行为；需要 smoke test。
- ASP.NET Core 依赖会扩大 MCP server project 依赖面；建议 server 代码独立 package，CLI 只引用 host facade。
- 前端 API 若只做通用 tool bridge 会降低 UX；MVP 应同时提供少量 typed convenience endpoints。
- local repo 数据敏感；默认 host 必须是 `127.0.0.1`，README 说明不要随意暴露到公网。
- dist/static-dir 路径在开发和发布环境不同；需要清晰 fallback。
