# 变更：新增 .NET LLM Wiki MCP 包

## 背景和动机 (Context And Why)

`cozo-lib-dotnet` 已有 `Om.CodeKnowledge` 和 `Om.Query`，能承载代码/文档知识图谱与 Datalog 查询。但 coding agent 需要的入口不是 C# facade 本身，而是类似 GitNexus 的本地工具：扫描仓库、写入图谱、生成 wiki，并通过 MCP stdio 暴露查询工具。

本 track 在 `cozo-lib-dotnet-llm-wiki/packages/` 下创建多个独立 .NET project，作为 Cozo OM 之上的应用层封装。

## "要做"和"不做" (Goals / Non-Goals)

**目标:**
- 创建 `Cozo.DotNet.LlmWiki.Core`：配置、请求/结果、工具模型。
- 创建 `Cozo.DotNet.LlmWiki.Indexing`：仓库扫描、语言识别、hash、轻量符号/文档抽取、CodeKnowledge batch indexing。
- 创建 `Cozo.DotNet.LlmWiki.Wiki`：基于 `WikiPlan` 和 facts 生成 Markdown wiki。
- 创建 `Cozo.DotNet.LlmWiki.McpServer`：CLI 主入口和 stdio MCP 服务。
- MCP tools 覆盖 index/query/context/impact/docs/wiki 的 MVP。
- 添加 smoke test project，验证 build、index、wiki、MCP JSON-RPC。

**非目标:**
- 不实现完整 Tree-sitter/Roslyn 语义解析。
- 不实现 embedding/vector search。
- 不实现 HTTP server/Web UI。
- 不实现 hooks/skills 自动安装。
- 不实现跨仓库 group/contract bridge。

## 变更内容（What Changes）

- 新增 `cozo-lib-dotnet-llm-wiki/packages/Cozo.DotNet.LlmWiki.Core/`
- 新增 `cozo-lib-dotnet-llm-wiki/packages/Cozo.DotNet.LlmWiki.Indexing/`
- 新增 `cozo-lib-dotnet-llm-wiki/packages/Cozo.DotNet.LlmWiki.Wiki/`
- 新增 `cozo-lib-dotnet-llm-wiki/packages/Cozo.DotNet.LlmWiki.McpServer/`
- 新增 `cozo-lib-dotnet-llm-wiki/tests/Cozo.DotNet.LlmWiki.Tests/`
- 新增 solution 和 README。

## 影响范围（Impact）

- 受影响的能力（behaviors）：`dotnet-llm-wiki`
- 受影响的代码：
  - `cozo-lib-dotnet-llm-wiki/**`（新增）
  - 不修改 `cozo-lib-dotnet/src/Om.Core`
