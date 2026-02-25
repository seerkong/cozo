---
knowledge_plane: domain
doc_role: canonical
status: active
context: code-knowledge
last_verified: 2026-07-06
---

# 观测层事实：符号图

观测层回答一个问题：**从代码文本与语义分析能证明什么**。它只落可重算的事实，索引数据永远可以从仓库重建（这也是 schema 演化采用 rebuild 式迁移的前提，见 [policies/schema-evolution.md](../policies/schema-evolution.md)）。

## Identity & Boundary

- `ck_repo` 是图的作用域根：一次索引对应一个 `repo_id`，记录仓库根路径与索引时的 commit。
- `ck_file` 是增量索引的对账单元：`hash` 列承载文件内容哈希，增量 diff 就是拿新哈希对老 `ck_file` 行（见 [workflows/indexing.md](../workflows/indexing.md)）。
- `ck_meta` 记录 `schema_version`（当前 v2）与 `indexed_at`/`indexed_commit` 等图级元数据；staleness 判断（索引是否落后 HEAD）依赖 indexed commit。

## Core Objects

### ck_symbol

一个可寻址的代码单元（类型/方法/字段/函数……）。语义要点：

- `sym_key` 是**跨重建的稳定键**：symbol_id 会随重索引变化，需要跨索引锚定（如 DEPA 标注再锚定）时用 sym_key。
- `resolver` 标记事实来源（`treesitter` 等）；Roslyn 增强只补 `doc_id`，不改符号的 resolver。
- `exported`/`visibility` 是入口点检测与 public_api 判定的输入。

### ck_edge

符号间的一条有向关系，key 为 `(from_id, to_id, kind, file_id, line)`——**边锚定到具体调用点**，同一对符号在不同行的调用是不同的边。三个值列是本对象的核心语义：

- `kind`：关系类别（CALLS / ACCESSES / IMPORTS / EXTENDS / IMPLEMENTS / METHOD_OVERRIDES / METHOD_IMPLEMENTS……）。
- `confidence`：0–1，这条边"是真的"的把握，分档语义见 [policies/edge-confidence.md](../policies/edge-confidence.md)。下游消费（trace、impact、hook 注入、DEPA 判读）都按 confidence 过滤或如实携带。
- `resolver` + `evidence`：谁断言了这条边、凭什么（如 `treesitter`+`ambiguous:3`、`roslyn`）。evidence 让弱证据可审计，而不是被冒充成强证据。

### ck_external_call

出仓（BCL/第三方）调用的**低保真摘要**：按 `(caller_id, target_key)` 聚合，只存第一处调用点作证据、`count` 承载其余，`category` 按副作用词表预分类（file_io/network/db/process/...）。它不是边——出仓目标不在图内，无法成边；DEPA 的 effect 维判读（如 V-E1 core 直接 IO、V-S4 mtime 判状态）以它为观测输入。

### ck_doc_block / ck_concept / ck_owner / ck_diagnostic

- `ck_doc_block`：文档切块（锚点+文本+哈希），供 doc↔symbol 关联与检索。
- `ck_concept`：命名概念节点，供 trace_concept 类查询。
- `ck_owner`：目标归属标注。
- `ck_diagnostic`：索引过程的非阻塞诊断（如 roslyn_failed、community_failed）——失败降级不失败索引，靠它留痕。

## Invariants

- 事实可重算：任何 `ck_*` 行都能通过重跑索引恢复，库本身不是事实源。
- 无候选不造边：解析不出目标的调用点只进计数，绝不产生低质边（宁缺勿滥）。
- add-only 扩展：同一 schema 版本内新增关系（如 ck_external_call）不得改变既有关系的重索引语义。
