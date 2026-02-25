---
knowledge_plane: global
doc_role: reference
status: active
last_verified: 2026-07-06
---

# MCP 工具速查

## Scope

18 个 depa-wiki MCP 工具的一行速查。**真源两处，本文不复制**：

- 逐工具参数/示例/接入配置 → 用户手册 [mcp-tools.md](../../../../cozo-lib-dotnet-llm-wiki/docs/mcp-tools.md)（P1，以 `depa-wiki tools` 实际输出为准）；
- 语义分组与不变量 → [modeling: llm-wiki/objects/tool-surface.md](../../../modeling/domain/contexts/llm-wiki/objects/tool-surface.md)。

## Table

| 组 | 工具 |
|----|------|
| 索引与构建 | `index_repo` · `index_embeddings` · `build_wiki` |
| 检索与图查询 | `semantic_search` · `overview_graph` · `symbol_context` · `impact_of_change` · `trace` · `check` · `detect_changes` |
| 文档与关系解释 | `docs_for_code` · `explain_relation` · `query_named` |
| 解析器 | `parser_status` · `parse_file` |
| DEPA | `depa_conformance` · `health_score` · `fact_grade_map` |

## Update Procedure

工具矩阵变更时以 `depa-wiki tools` 输出为准更新用户手册（脚本比对 missing=[] 验收），本表只跟随组划分；不一致时以运行器工具矩阵为真源。
