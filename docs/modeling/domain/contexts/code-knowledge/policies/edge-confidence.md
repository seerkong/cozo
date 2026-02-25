---
knowledge_plane: domain
doc_role: canonical
status: active
context: code-knowledge
last_verified: 2026-07-06
---

# 边置信度与双解析器合并

## Rule

每条 `ck_edge` 必须如实携带其解析强度（`confidence` + `resolver` + `evidence`）；弱证据可以入图，但**不得冒充强证据**；解析不出候选的调用点只计数、不造边。

## tree-sitter 侧分档（CallResolver）

| 档 | 含义 | evidence 示例 |
|----|------|---------------|
| 0.9 | 唯一精确解析：`new T()`、本类型/继承链成员、receiver 绑定类型且 arity 吻合、repo 唯一类型静态调用 | `new` / `member` / `binding:<T>` / `static-type` |
| 0.7 | 名称唯一但弱一档：import 约束下唯一、repo 级名称唯一、绑定命中但 arity 不吻合 | `import` / `repo-unique` |
| 0.5 | 歧义解析：多候选中取确定性首个 | `ambiguous:<n>` |
| —— | 无候选：只进 unresolved 计数，**不产生边** | |

兜底过滤：进入歧义池的目标限 callable kinds（method/function/constructor/delegate/constant/variable），namespace/type/doc 不参与兜底——避免"同名撞类型"的假边。

## Roslyn 增强与合并优先级（RoslynMergeStep）

C# 文件在 tree-sitter 基线之后跑 Roslyn 语义分析，按 `(callerSymbolId, file, line)` 合并：

- caller 与 callee 的 DocId 都能映射回基线符号时，**Roslyn 边替换该调用点的全部 tree-sitter CALLS 边**（confidence 1.0 精确 / 0.6 候选，resolver=`roslyn`）——语义解析优先于启发式解析。
- 基线没有边的调用点（隐式 `new()`、未解析点）经同一路径插入；映射失败（合成构造器、非 tree-sitter 文件）保留基线边并计入 unmapped。
- 出仓调用不入边，进入 `ck_external_call` 摘要通道。
- 符号侧 Roslyn 只回填 `doc_id`，resolver 保持 `treesitter`——只有它产的边才标 `roslyn`。

## Failure Semantics

Roslyn 编译失败**绝不失败索引**：记一条 `roslyn_failed` warning 诊断、返回零计数，tree-sitter 基线原样成立。tree-sitter 完全解析失败时回退 regex 抽取（行为等同引入 tree-sitter 之前）。

## Enforcement Points

- `CallResolver`（分档与兜底过滤）、`RoslynMergeStep`（合并与替换）——见 [code-map.md](../code-map.md)。
- 下游消费按 confidence 过滤：trace 默认 MinConfidence 0.7、hook augment 取 ≥0.7 的 callers/callees、DEPA violation 如实携带观测边 confidence（0.5 歧义解析不冒充 1.0）。
