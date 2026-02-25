# 方案设计：混合检索

## 上下文

Cozo FTS 语法（cozo-core/src/runtime/tests.rs 已查证）：
- 建索引：`::fts create <rel>:<idx> { extractor: <col>, tokenizer: Simple|Ngram..., filters: [Lowercase, ...] }`
- 查询：`?[k, v, s] := ~<rel>:<idx>{k, v | query: $q, k: <topk>, bind_score: s}`（BM25 分）
现有向量检索：CozoVectorSearchService（HNSW + provider 降级链）。

## 方案概览

1. **FTS 索引**（VectorSearch capsule）
   - `EnsureFtsIndexAsync`：ck_symbol 需要单列 extractor → 建一张检索投影表 ck_search_text {source_id => source_kind, text}（索引期由符号 name+signature 与 doc_block text 合成，:replace 重建；避免改 ck_* 主表），其上 `::fts create ck_search_text:fts { extractor: text, tokenizer: <selected>, filters: [Lowercase] }`。
   - tokenizer 选择：track 内实测中文注释样例——Simple 对 CJK 无分词则用 `NgramTokenizer`（min 1 max 2 或库支持的参数形态，以 cozo-core 实际参数为准）；结论记 findings。
   - 幂等：::fts create 冲突捕获（对齐 schema init 的 IsCreateConflict 模式）。
2. **文本检索**：`~ck_search_text:fts{source_id, source_kind, text | query: $q, k: $k, bind_score: s}`；查询词字面化：按空白切 token、每个 token 转义/引号包裹后 OR 连接（防表达式注入）。
3. **RRF 融合**：两通道各取 top-k（k = limit*4 上限 50）；score=Σ 1/(K+rank_channel)；同 source_id 合并；输出沿用现有 SemanticSearchHit 结构（add-only：channel 命中标记与 rrf 分数字段）。
4. **工具升级**：semantic_search 参数 mode/rrfK（add-only，默认 hybrid/60）；Server API 请求体同步；smoke：精确标识符命中、mode 三态、降级。
5. **索引管线**：RepositoryIndexer 索引完成后重建 ck_search_text 并 ensure FTS（失败 → search_index_failed 诊断不炸索引）；embeddings 仍是独立步骤（index_embeddings 工具）。

## 影响范围与修改点（Impact）

- VectorSearch：HybridSearch 逻辑 + FTS ensure（internal 实现，公开面只在服务方法参数 add-only）。
- Indexing：ck_search_text 重建接线。
- Tools/Server：参数与 smoke。

## 决策摘要

- RRF K=60（mission G1 契约）；检索投影表方案（不改 ck_* 主表、单列 extractor 约束下最简）。

## 风险 / 权衡

- FTS 表达式注入 → 字面化 token（delta literal-query case）。
- ck_search_text 冗余存储 → 只存 name+signature/text 截断（每条 ≤512 字符），可重建。
- 中文分词质量 → Ngram 兜底，实测记录。

## 最小公开面

实际口径（ED-1）：公开面 = `CozoVectorSearchService.HybridSearchAsync` 新服务方法 + 现有模型的 add-only 字段（`VectorSearchHit.RrfScore`/`Channels`、`VectorSearchResult.Mode`/`Diagnostics`）。其余新增一律 internal：`TextSearchHit`、`TextSearchAsync`、`EnsureSearchTextAsync`、`EnsureFtsIndexAsync`。跨程序集调用（Indexing 的 BuildSearchIndex 步骤、测试）经 `InternalsVisibleTo`（Cozo.DotNet.LlmWiki.Indexing / Cozo.DotNet.LlmWiki.Tests）。静态 internal 方法保留静态形态，避免 Indexing 侧为调用它们而实例化服务、加载 ONNX embedding provider。
