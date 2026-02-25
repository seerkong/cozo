---
knowledge_plane: domain
doc_role: canonical
status: active
context: code-knowledge
last_verified: 2026-07-06
---

# 索引流程：全量、增量与派生层物化

## Trigger

首次接入仓库、代码变更后保鲜、schema 版本升级（强制 reindex）。运维命令见 [docs/impl/global/howto/index-and-incremental-ops.md](../../../../../impl/global/howto/index-and-incremental-ops.md)。

## 全量索引（建图主链）

1. **枚举与解析**：遍历仓库文件，tree-sitter 为主路径抽取符号与 IMPORTS/EXTENDS/IMPLEMENTS；tree-sitter 完全失败的文件回退 regex 抽取。
2. **调用解析**：`CallResolver` 把解析出的调用点变成 CALLS/ACCESSES 边并分档置信度（[policies/edge-confidence.md](../policies/edge-confidence.md)），继承边后处理出 METHOD_OVERRIDES/METHOD_IMPLEMENTS。
3. **Roslyn 语义合并**：C# 源交给 `RoslynMergeStep`，按调用点替换/补插边、回填 doc_id；出仓调用汇入 `ck_external_call`；失败降级留诊断。
4. **落库**：批量写 `ck_repo/ck_file/ck_symbol/ck_edge/...`，记录 indexed HEAD commit 到 `ck_meta`（staleness 与 detect_changes 的比较基准）。

## 增量索引（IncrementalDiff）

基线 = 同仓库既有 `ck_file` 行的内容哈希；当前侧 = 新解析批次的哈希。逐文件比对得到 Added / Changed / Removed / Reused 四类：

- Changed/Removed 的文件先走 `RemoveFileFactsAsync` 清除旧事实，再对 Added/Changed 重建；Reused 文件零重写。
- **不问 git**：基线可能是从 dirty 工作树索引的，commit-range diff 会与库内实际内容不一致——哈希对账对 git 与非 git 仓库同样精确。
- 无基线 ⇒ 回退全量路径。

**一致性承诺**：增量索引结果与全量重建**等价**（一致性门禁测试保障）。怀疑不一致时的处置见 [docs/impl/global/troubleshooting/](../../../../../impl/global/troubleshooting/index.md)。

## 派生层物化

索引完成后按开关依次物化，每步失败都降级为 warning 诊断、不影响已建索引：

1. `ComputeCommunitiesAsync`：Louvain 社群 `:replace` 写 `ck_community/ck_member`。
2. `ExtractProcessesAsync`：入口点检测（图级 + 语法级候选合并）→ BFS 执行流，写 `ck_entry_point/ck_process/ck_process_step`。
3. 搜索索引（embeddings 等）幂等重建。

对象语义见 [objects/derived-layers.md](../objects/derived-layers.md)。

## Failure Semantics

单文件解析失败不失败仓库索引；Roslyn/社群/执行流/搜索各环节失败均记 `ck_diagnostic` 并继续——索引流程的唯一硬失败是 schema 版本不符且未授权 reindex（[policies/schema-evolution.md](../policies/schema-evolution.md)）。
