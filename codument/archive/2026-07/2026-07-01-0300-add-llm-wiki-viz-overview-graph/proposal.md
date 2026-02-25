# 变更：LLM Wiki 可视化概览图与类别过滤

## 背景和动机 (Context And Why)
当前 `cozo-wiki` 可视化工作台需要先执行搜索才能看到图，探索体验偏点状。已有 repo DB，例如 `/Users/kongweixian/ai/eidolon/eidolon-anchor/.cozo-wiki`，已经包含文件、符号、文档块和关系事实，应该能直接加载宏观项目图。同时，语义搜索需要允许用户限定结果来自文档还是代码，避免混在一起时干扰判断。

## "要做"和"不做" (Goals / Non-Goals)
**目标:**
- 让 `semantic_search` 支持类别集合过滤，v1 支持 `code` 与 `docs`。
- 新增整体概览图能力，直接从 CodeKnowledge facts 加载 bounded graph。
- 前端增加 code/docs 类别控件和“加载整体图”入口。
- 保持 MCP、CLI、HTTP 共享同一套 tool runner 行为。

**非目标:**
- 不改变 repo indexing 的事实模型。
- 不做全仓库无限图 dump。
- 不新增 raw CozoScript HTTP endpoint。
- 不要求重新生成已有 `.cozo-wiki` 数据库；已有 CodeKnowledge facts 应可直接用于概览图。

## 变更内容（What Changes）
- 扩展 vector search request，按 `source_kind` 过滤 `symbol` / `doc` embedding rows。
- 新增 shared tool `overview_graph`，返回 `{ nodes, edges, totalNodes, totalEdges, truncated }`。
- Server 新增 `POST /api/graph/overview`。
- Vue 工作台新增 code/docs category controls，并在 graph tab / toolbar 增加加载整体图按钮。
- README 补充类别搜索和整体图 API 用法。

## 影响范围（Impact）
- 受影响的能力（behaviors）：`llm-wiki-viz`
- 受影响的代码：
  - `cozo-lib-dotnet-llm-wiki/packages/Cozo.DotNet.LlmWiki.VectorSearch`
  - `cozo-lib-dotnet-llm-wiki/packages/Cozo.DotNet.LlmWiki.Tools`
  - `cozo-lib-dotnet-llm-wiki/packages/Cozo.DotNet.LlmWiki.Server`
  - `cozo-lib-dotnet-llm-wiki-viz/src`
  - `cozo-lib-dotnet-llm-wiki/tests`
