# 变更：新增 embedding/vector search

## 背景和动机 (Context And Why)

CozoDB 已有向量类型和 HNSW 检索能力。当前 LLM Wiki 只支持结构化图查询与 wiki 生成，缺少基于文本语义的相似检索。用户要求基于 CozoDB 已有能力实现 embedding/vector search。

## "要做"和"不做" (Goals / Non-Goals)

**目标:**
- 新增 `Cozo.DotNet.LlmWiki.VectorSearch` project。
- 定义 embedding provider 抽象。
- 提供 deterministic embedding provider 用于离线 smoke tests。
- 在 CozoDB 中创建 `llm_wiki_embedding` 向量 relation。
- 实现 embedding indexing 和 semantic search。
- MCP tools 增加 `index_embeddings` 和 `semantic_search`。

**非目标:**
- 不绑定单一商业 embedding API。
- 不引入外部向量数据库。
- 不在第一版要求 HNSW 必须可用；保留 HNSW 初始化入口，默认 exact cosine search 保证可测试。

## 变更内容（What Changes）

- 新增 VectorSearch 包。
- McpServer 引用 VectorSearch。
- Tests 增加向量索引与 semantic search smoke。
- README 增加向量检索说明。

## 影响范围（Impact）

- 受影响能力：`dotnet-llm-wiki-vector-search`
- 受影响代码：
  - `cozo-lib-dotnet-llm-wiki/packages/Cozo.DotNet.LlmWiki.VectorSearch/**`
  - `cozo-lib-dotnet-llm-wiki/packages/Cozo.DotNet.LlmWiki.McpServer/**`
  - `cozo-lib-dotnet-llm-wiki/tests/**`
