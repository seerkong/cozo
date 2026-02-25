---
knowledge_plane: domain
doc_role: canonical
status: active
context: llm-wiki
last_verified: 2026-07-06
---

# 混合搜索：多通道与 RRF 融合

`semantic_search` 是检索组的排序核心：把多个独立通道的结果融合成一个可解释的排名。

## Structure

- **通道**：向量通道（embeddings 相似度，需先 `index_embeddings`）+ BM25 文本通道（整词匹配符号名与签名声明文本）。mode 选择通道组合。
- **融合**：Reciprocal Rank Fusion——`score = Σ_channels 1/(rrfK + rank)`，rank 按通道内 1-based 名次，`rrfK` 默认 60。

## Semantics

- RRF 只看**名次**不看原始分：两个通道的分值量纲不可比（余弦距离 vs BM25 分），按名次融合避免任何一方的分值刻度支配结果。
- 结果携带 `RrfScore` 与命中通道列表——排名可解释：知道一条命中来自哪个通道、各占多少名次贡献。
- **降级**：embeddings 未建或向量通道不可用时，混合搜索安全降级为纯 BM25 文本通道——检索永远可用，只是召回变窄（eval oracle 即以此模式跑门禁）。

## Invariants

- 通道彼此独立：一个通道失败不影响另一个通道的名次。
- `rrfK` 下限保护（≥1），避免除零或名次权重爆炸。

实现：`CozoVectorSearchService.HybridSearchAsync`（见 [code-map.md](../code-map.md)）。使用参数与示例见用户手册 [mcp-tools.md](../../../../../../cozo-lib-dotnet-llm-wiki/docs/mcp-tools.md)。
