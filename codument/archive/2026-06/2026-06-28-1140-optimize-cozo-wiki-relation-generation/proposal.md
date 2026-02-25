# 变更：优化 cozo-wiki 关系生成，避免 repo DB 膨胀

## 背景和动机

在 eidolon-anchor 真实仓库上，cozo-wiki 索引得到 1874 个文件、18387 个符号、4401 个文档块，但生成了 1708857 条关系，SQLite DB 达到 2.2G。根因是 `RepositoryIndexer.LinkDocsToSymbols` 对所有 doc block 与所有 symbol 做全局文本包含匹配，并把弱文本命中全部持久化成 `documents` 边。

GitNexus 的相关做法更克制：结构边预物化，弱文本关联通过 BM25/search 按需 top-k 召回；昂贵推断路径都有明确 cap/budget。cozo-wiki 应采用同类策略。

## 目标

- 默认取消全局 doc-symbol 笛卡尔积匹配。
- 默认只生成局部、高置信、top-k 的 `documents` 关系。
- 为 `RepositoryIndexRequest` 和 CLI/MCP `index_repo` 暴露 doc-symbol link 模式与预算。
- 用测试证明关系数量被约束，同时局部文档链接仍可用。
- 在 eidolon-anchor 使用隔离 data folder 复测并观察 DB 大小。

## 非目标

- 不实现完整 BM25/FTS 查询引擎。
- 不改变 Cozo CodeKnowledge schema。
- 不删除既有 `docs_for_code` 工具。
- 不在本 track 中实现增量索引或旧 DB 自动清理。

## 变更内容

- `RepositoryIndexRequest` 增加 `DocSymbolLinkMode`、`MaxDocLinksPerDoc`、`MaxInferredRelations`。
- `RepositoryIndexer` 将 `LinkDocsToSymbols` 改为局部候选、名称过滤、去重、排序、预算控制。
- `index_repo` 工具 schema/help 支持可选参数。
- README 说明默认策略与如何显式启用全局模式。
- 测试加入大规模重复命名 fixture，防止关系数量回退。

## 影响范围

- 受影响的能力：`cozo-wiki-indexing`
- 受影响的代码：
  - `cozo-lib-dotnet-llm-wiki/packages/Cozo.DotNet.LlmWiki.Core/LlmWikiModels.cs`
  - `cozo-lib-dotnet-llm-wiki/packages/Cozo.DotNet.LlmWiki.Indexing/RepositoryIndexer.cs`
  - `cozo-lib-dotnet-llm-wiki/packages/Cozo.DotNet.LlmWiki.McpServer/Program.cs`
  - `cozo-lib-dotnet-llm-wiki/tests/Cozo.DotNet.LlmWiki.Tests/Program.cs`
  - `cozo-lib-dotnet-llm-wiki/README.md`
