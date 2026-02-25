# Decisions

## Usage

本文件记录 `add-dotnet-llm-wiki-viz` mission 期间需要保持的技术/产品决策。durable 决策后续可拆入 `decisions/` 并在 archive-mission 时提升。

### 1. 【P0】任务尺度

- 背景：用户希望在 `cozo-lib-dotnet-llm-wiki` 上新增前端可视化、server mode 和交互式 repo DB 操作。
- 最终决策：创建独立 mission `add-dotnet-llm-wiki-viz`，由多个 track 分别落地共享工具层、server/CLI、前端、验证文档。
- 决策理由：需求跨 CLI、HTTP API、前端可视化和 E2E，执行期可能需要根据实际 API/UX 调整 track 切片。
- 状态：accepted

### 2. 【P1】前端技术栈

- 背景：现有 `cozo-lib-bun-viz/frontend` 已验证 Vue 3 + Vite + vis-network 在本仓库中的可视化工具形态。
- 最终决策：`cozo-lib-dotnet-llm-wiki-viz` 使用 Vue 3 + Vite + TypeScript，参考但不复制 ontology/governance demo 页面。
- 决策理由：降低新增前端栈成本，同时让 repo knowledge UI 贴近已有可视化工具的工程风格。
- 状态：accepted

### 3. 【P1】Server 边界

- 背景：当前 `LlmWikiToolRunner` 位于 `McpServer/Program.cs` 内部，若直接做 HTTP server 容易复制逻辑。
- 最终决策：先抽出可复用 tool runner/contracts，再新增 `Cozo.DotNet.LlmWiki.Server` 提供 ASP.NET Core minimal API；`McpServer` CLI 负责 `--serve` 启动。
- 决策理由：保持 MCP/CLI/HTTP 使用同一能力源，降低漂移。
- 状态：accepted

### 4. 【P1】查询能力暴露面

- 背景：raw CozoScript 强大但需要额外安全、语法、错误和 UX 设计。
- 最终决策：MVP 不开放 raw CozoScript endpoint，先通过 `query_named`、semantic search 和 tools bridge 释放查询能力。
- 决策理由：更贴合本地工作台的安全边界，也复用已有 NamedQuery 能力。
- 状态：assumed
