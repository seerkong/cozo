---
knowledge_plane: domain
doc_role: canonical
status: active
context: llm-wiki
last_verified: 2026-07-06
---

# 降级与预算纪律

## Rule

产品面的每个可选依赖（LLM、embeddings、tree-sitter CLI、Roslyn、索引库本身）失效时，系统必须**降级而非失败**；注入 agent 会话的内容必须**有界**。

## 降级阶梯（各失效点的既定行为）

| 失效点 | 降级行为 |
|--------|----------|
| hook 任何异常（无库/无命中/内部错误） | **静默降级**：stdout 空、exit 0，诊断只写 stderr——绝不打断或污染 agent 会话 |
| LLM 不可用（build_wiki --use-llm false 或调用失败） | 骨架与结构事实照常产出，narrative 槽位留 `_pending` 占位——**这是正常路径不是错误** |
| embeddings 未建 | `semantic_search` 降级为 BM25 文本通道（[objects/hybrid-search.md](../objects/hybrid-search.md)） |
| tree-sitter CLI 缺失（TSCLI001） | `parse_file` 类工具报诊断；索引侧回退 regex 抽取，基础索引与检索可用 |
| Roslyn 编译失败 | 记 warning 诊断，tree-sitter 基线索引原样成立（见 [code-knowledge policies](../../code-knowledge/policies/edge-confidence.md)） |

## Rationale

hook 与工具面处在 agent 关键路径上：一次异常弹窗/报错的代价（打断会话、注入噪声）远高于一次静默缺失。代价是"没生效不易察觉"——用 `hooks status`、stderr 诊断与 [排障手册](../../../../../impl/global/troubleshooting/hooks-integration.md) 补偿可观测性。

## 预算有界

- hook augment 注入块字符预算默认 2000（`--budget` 可调），超额截断。
- wiki 生成的 context/workflow 数量、执行流步数、社群数量均有显式上限，截断必须计数进 diagnostics——**有界性靠预算+审计双保险**，不允许静默丢失。

## Enforcement Points

HookCommands（silent-degrade 与 budget 测试）、FractalWikiPipeline（预算 diagnostics）、CozoVectorSearchService（BM25 降级）——见 [code-map.md](../code-map.md)。
