---
knowledge_plane: domain
doc_role: canonical
status: active
context: llm-wiki
last_verified: 2026-07-06
---

# 工具面：18 个 MCP 工具的语义分组

工具面是图谱的**唯一产品化查询边界**：MCP stdio（给 agent）、CLI `call`（给人）、HTTP server（给工作台）三种暴露共享同一个运行器与同一套工具矩阵。逐工具参数与真实示例是用户手册的真源（[mcp-tools.md](../../../../../../cozo-lib-dotnet-llm-wiki/docs/mcp-tools.md)），本叶只固化语义分组——每组回答一类问题：

| 语义组 | 工具 | 回答什么 |
|--------|------|----------|
| 索引与构建 | `index_repo` `index_embeddings` `build_wiki` | 把仓库变成图 / 向量 / 文档 |
| 检索与图查询 | `semantic_search` `overview_graph` `symbol_context` `impact_of_change` `trace` `check` `detect_changes` | 找符号、看邻域、算影响、寻路径、查环、比对索引与 HEAD |
| 文档与关系解释 | `docs_for_code` `explain_relation` `query_named` | 代码↔文档互查、两符号关系解释、命名查询直达 |
| 解析器 | `parser_status` `parse_file` | 语义解析后端可用性与单文件解析 |
| DEPA 架构符合度 | `depa_conformance` `health_score` `fact_grade_map` | 架构判读报告（判读语义见 [depa context](../../depa/index.md)） |

## Invariants

- **矩阵即真源**：工具集合由运行器的工具矩阵单点定义，`tools` 子命令输出、MCP 注册、手册覆盖都对齐它（手册以脚本比对 missing=[] 验收）。
- **图证据可溯源**：查询类工具输出携带 path:line 级 source 证据，且基于 `indexedCommit` 快照——索引落后 HEAD 时结果可能与工作区不对齐，由 `detect_changes`/staleness hook 显式暴露而不是静默容忍。
- **工作目录寻库**：工具按 work-dir 解析 `.depa-wiki/depa-wiki.db`；无库时按 [policies/degradation-and-budget.md](../policies/degradation-and-budget.md) 的降级纪律行事。

## 量化价值锚点

真实 agent 双模式 eval（codex，24 题）给出工具面贡献的量化证据：baseline avg 0.096 → native（18 工具）0.950，must 要素命中率 7.7% → 96.2%。跑法见 [docs/impl/global/howto/eval-run.md](../../../../../impl/global/howto/eval-run.md)。
