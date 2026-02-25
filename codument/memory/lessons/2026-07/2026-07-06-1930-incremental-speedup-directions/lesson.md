# lesson: incremental-speedup-directions

Memory URI: memory://lessons/incremental-speedup-directions
Source: archive://2026-07-06-1930-add-llm-wiki-incremental-indexing

# 增量索引提速深化方向（2.9x → ~5x）

本仓 dogfood 中位 2.9x（目标 3x 边界未稳达）。分段归因：facts 写入+search text ~1s、社群+process ~0.7s、Roslyn ~0.27s、解析 ~0.2s。按收益排序的深化方向：① ParsedFileResult 按 ck_file.hash 缓存 ② Roslyn 增量归并 ③ 派生层选择性重算。一致性门禁（ConsistencyGateTests 13 表四轮）是任何深化的回归底线。
