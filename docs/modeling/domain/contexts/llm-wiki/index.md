---
knowledge_plane: domain
doc_role: guide
status: active
context: llm-wiki
last_verified: 2026-07-06
---

# LLM Wiki Context

## 目录职责

- **holds**：depa-wiki 产品面的领域语义边界——MCP 工具族、混合搜索、双分形 wiki 生成管线、hooks 被动注入与 skills 生成，及其降级/预算/所有权纪律。
- **excludes**：底层 `ck_*` 图谱本体（→ `code-knowledge` context）；DEPA 判读语义（→ `depa` context）；面向使用者的操作手册（→ `cozo-lib-dotnet-llm-wiki/docs/`，本树只引用）。
- **tier**：`stable`
- **promotes_from**：llm-wiki 各 track（tools/hooks/skills/fractal-wiki/eval）中经测试与 dogfood 验证的稳定语义
- **promotes_to**：LlmWiki 各包实现、测试与用户手册

## Boundary

本 context 拥有"代码知识图谱如何被消费"的语义：18 个 MCP 工具各自回答什么问题、搜索结果怎么排序、wiki/skills 这类生成物的确定性与边界承诺，以及产品面横切的三条纪律（静默降级、预算有界、own-prefix 所有权）。

## Not Owned Here

- **code-knowledge**：图里的事实与派生层语义在 [code-knowledge context](../code-knowledge/index.md)——本 context 只消费，不重述。
- **depa**：`depa_conformance`/`health_score` 工具的**判读语义**（GAP/PASS/BLOCKED、规则口径）在 [depa context](../depa/index.md)；本 context 只拥有其工具面形态。

| 类目 | 职责 | 何时阅读 |
|------|------|----------|
| [objects/](objects/index.md) | 工具面分组与混合搜索、生成物对象 | 想知道产品面有什么、各自回答什么 |
| [policies/](policies/index.md) | 降级/预算/所有权三条横切纪律 | 要理解失败行为与安全边界 |
| [workflows/](workflows/index.md) | wiki 双管线生成、hooks/skills 流程 | 要理解生成物是怎么长出来的 |

## Code Map

- [code-map.md](code-map.md)
