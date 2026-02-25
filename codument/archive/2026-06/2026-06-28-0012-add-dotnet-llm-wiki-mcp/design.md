# Design：.NET LLM Wiki MCP MVP

## 上下文

目标是在 Cozo OM 的应用层补 GitNexus-like agent 工具面。已有 `Om.CodeKnowledge` 是图事实源，`Om.Query` 是 Datalog 查询层；新包负责把文件系统和 MCP/stdin/stdout 接上去。

## 方案概览

1. 包结构
   - `Cozo.DotNet.LlmWiki.Core`：Data capsule，放模型和配置。
   - `Cozo.DotNet.LlmWiki.Indexing`：Effect + Processor capsule，读取文件系统并生成 `CodeKnowledgeBatch`。
   - `Cozo.DotNet.LlmWiki.Wiki`：Processor capsule，把 `WikiPlan` 编译为 Markdown。
   - `Cozo.DotNet.LlmWiki.McpServer`：Actor capsule，CLI 与 MCP stdio。
2. 数据流
   - `repo path` -> scanner -> extracted facts -> `CodeKnowledgeBatch` -> `CozoOm.IndexCodeKnowledgeAsync`
   - `CozoOm.BuildWikiPlanAsync` -> wiki compiler -> Markdown files / in-memory result
   - MCP tool -> app service -> existing `Om.CodeKnowledge` facade -> JSON result
3. MCP 工具
   - `index_repo`
   - `build_wiki`
   - `symbol_context`
   - `impact_of_change`
   - `docs_for_code`
   - `explain_relation`
   - `query_named`

## 影响范围与修改点（Impact）

- 新增 packages，不改变现有 OM API。
- 新增测试 project，用 mem Cozo engine 做 smoke。
- 主入口 stdout 只输出 MCP JSON-RPC；普通日志写 stderr。

## 决策摘要

- 轻量 extractor 用启发式规则；后续可替换为 Roslyn/Tree-sitter。
- MCP stdio 第一版手写 JSON-RPC 2.0 协议子集，避免引入不稳定依赖。
- DB 默认使用 mem engine；可通过 CLI option 指定 engine/path。

## 风险 / 权衡

- 轻量符号抽取准确度有限：通过 evidence 和文件/行号保证可解释，不承诺完整调用图。
- MCP 协议只覆盖 tools 子集：足够 agent 查询，resources/prompts 后续扩展。
- 当前 wiki 是结构化 Markdown 编译，不调用 LLM 生成摘要，避免网络和模型依赖。
