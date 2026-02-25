# Decisions

## Usage
- 用于记录需要用户确认的决策问题、选项、最终结论与理由
- 问题标题不用字母前缀；字母只用于选项
- 后续执行过程中出现的新决策，也继续追加到本文件，不新建分散的决策记录

### 1. 【P0】Server 项目形态
- 背景：需要新建 `cozo-lib-dotnet-viz-server`，兼容 Bun viz 后端。
- 需要决定：第一版是否采用 ASP.NET Core minimal API。
- 选项：
  - A) ASP.NET Core minimal API 项目
  - B) class library + 用户自行 host
  - C) 其他（可填写）
- 当前建议：A
- 用户答复：
- 最终决策：
- 决策理由：
- 状态：pending

### 2. 【P1】是否 serve 前端静态资源
- 背景：用户目标是兼容前端需要的后端部分；静态资源服务不是硬性要求。
- 需要决定：server 是否同时托管 `cozo-lib-bun-viz/frontend/dist` 或未来 .NET 目录下的前端资产。
- 选项：
  - A) v1 只提供 API，前端仍由 Vite/静态服务器托管
  - B) v1 同时支持可选静态资源托管
  - C) 其他（可填写）
- 当前建议：A
- 用户答复：
- 最终决策：
- 决策理由：
- 状态：pending

### 3. 【P1】Demo catalog 迁移方式
- 背景：Bun server demo modules 是 JS 代码，含 seed data、query definitions 和 handlers。
- 需要决定：C# 版第一版如何迁移 demo catalog。
- 选项：
  - A) C# 静态 demo definitions，避免 Node/Bun runtime 依赖
  - B) 从 Bun demo 导出 JSON fixtures，C# 加载数据并手写 handlers
  - C) C# server 运行时调用 Node/Bun（不推荐）
- 当前建议：A
- 用户答复：
- 最终决策：
- 决策理由：
- 状态：pending

### 4. 【P0】核心 OM capsule 命名
- 背景：`ingestBatch`、`impactAnalysis`、`ownershipTree`、`riskHotspot` 已决定不直接进入核心 OM，而是拆成依赖核心本体能力的功能 capsule。
- 需要决定：原 `cozo-lib-dotnet/src/Om/` 是否继续使用泛称 `Om`，还是改名以区分核心本体与功能 capsule。
- 用户答复：原来的 `cozo-lib-dotnet/src/Om/` 应当重命名为 `cozo-lib-dotnet/src/Om.Core`。
- 最终决策：将原核心 OM capsule 路径命名为 `cozo-lib-dotnet/src/Om.Core/`；`Om.Batch` 与 `Om.Analytics` 作为依赖 Om.Core 的独立功能 capsule。
- 决策理由：避免核心本体 capsule 与基于本体的 batch/analytics 功能 capsule 混淆，强化 DEPA capsule 边界。
- 状态：confirmed
