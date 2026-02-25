---
knowledge_plane: domain
doc_role: guide
status: active
context: depa
last_verified: 2026-07-06
---

# DEPA Context

## 目录职责

- **holds**：DEPA 架构符合度**解释层**的领域语义边界——`depa_*` 判读本体（类型/关系）、violation 与 verdict 语义（GAP/PASS/BLOCKED）、规则覆盖口径、标注与豁免纪律、扫描到报告的流程。
- **excludes**：被判读的 `ck_*` 观测事实（→ `code-knowledge` context）；判据条文与规则映射真源（→ `cozo-lib-dotnet/src/Om.Depa/rubrics/`，本树只引用）；报告解读操作指南（→ 用户手册 `depa-report.md`）。
- **tier**：`stable`
- **promotes_from**：add-llm-wiki-depa-ontology / expand-depa-detection-rules / refine-depa-detection-precision 等 track 中经 fixture 测试与本仓复扫验证的稳定语义
- **promotes_to**：Om.Depa 实现、检测器测试与 rubrics 机检

## Boundary

本 context 拥有"观测层之上如何做架构判读"的语义：什么是 capsule/contract/impl 这些判读实体、一次违规命中意味着什么、三种 verdict 的边界、规则分母怎么算、误报为什么走标注侧处置。核心立场：**DEPA 是解释层**——它不产生新代码事实，只对 `ck_*` 事实附加人工/配置/启发式判断，且每个判断携带 provenance 与 confidence。

## Not Owned Here

- **code-knowledge**：`ck_edge` 置信度、`ck_external_call` 摘要等观测语义见 [code-knowledge context](../code-knowledge/index.md)——DEPA 消费并如实携带，不重定义。
- **llm-wiki**：`depa_conformance`/`health_score` 的工具面形态见 [llm-wiki context](../llm-wiki/index.md)。

| 类目 | 职责 | 何时阅读 |
|------|------|----------|
| [objects/](objects/index.md) | depa_* 本体与符合度报告的结构语义 | 想知道判读实体和报告长什么样 |
| [policies/](policies/index.md) | 规则覆盖口径、verdict 语义、标注与豁免纪律 | 要解读结论或处置误报 |
| [workflows/](workflows/index.md) | 扫描→检测→物化→报告与整改闭环 | 要理解结论怎么产生、怎么消化 |

## Code Map

- [code-map.md](code-map.md)
