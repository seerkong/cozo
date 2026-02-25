# Design：Embedding / Vector Search

## 上下文

CozoDB 支持向量字段和距离函数。为了先得到可运行能力，第一版使用 deterministic embedding provider 做本地测试；后续可接 OpenAI/Ollama/ONNX provider。

## 方案概览

1. 新增 `VectorSearch` package。
2. 定义：
   - `IEmbeddingProvider`
   - `EmbeddingVector`
   - `VectorIndexRequest`
   - `VectorSearchRequest`
   - `VectorSearchResult`
3. Cozo relation：
   - `llm_wiki_embedding { item_id: String => source_kind: String, source_id: String, text: String, vector: <F32; 32>, updated_at: String }`
4. Indexing：
   - 从 `ck_symbol` 和 `ck_doc_block` 读取文本。
   - 生成 vector 并写入 relation。
5. Search：
   - 对 query 生成 vector。
   - 使用 `cos_dist(vector, $query)` 排序。
6. MCP:
   - `index_embeddings`
   - `semantic_search`

## HNSW 说明

Cozo HNSW 语法已在源码测试中确认：

```cozoscript
::hnsw create relation:index {
  fields: [vector],
  dim: 32,
  m: 32,
  distance: Cosine
}
```

MVP 默认 exact cosine search，避免 HNSW 创建/更新差异阻塞 smoke tests；保留 `EnsureHnswIndexAsync` 方法作为后续加速入口。

## 风险 / 权衡

- Deterministic embedding 只用于测试，语义效果有限。
- Exact scan 对大规模仓库不够快；后续应启用 HNSW。
