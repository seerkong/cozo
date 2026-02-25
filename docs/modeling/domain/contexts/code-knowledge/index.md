---
knowledge_plane: domain
doc_role: guide
status: active
context: code-knowledge
last_verified: 2026-07-06
---

# Code Knowledge Context

## 目录职责

- **holds**：`ck_*` 代码知识图谱观测层的领域语义边界——仓库/文件/符号/边/文档块等事实关系，以及社群、执行流、入口点等派生层，含 schema 演化与索引一致性语义。
- **excludes**：工具面与 wiki 生成（→ `llm-wiki` context）；对图谱的架构判读（→ `depa` context）；索引与增量的运维步骤（→ `docs/impl/global/howto/`）。
- **tier**：`stable`
- **promotes_from**：schema v2、增量索引、call-resolution、社群/执行流等 track 中经测试验证的稳定语义
- **promotes_to**：Om.CodeKnowledge / LlmWiki.Indexing 实现、测试与行为登记

## Boundary

本 context 拥有"仓库代码如何被观测成图"的语义：什么算一个符号、一条边的置信度与来源意味着什么、出仓调用如何被摘要、派生层（社群/执行流/入口点）由什么规则物化，以及索引重建/增量的等价性承诺。它是 **观测层**：只记录能从代码文本与语义分析证明的事实，不做架构判断。

## Not Owned Here

- **depa**：`depa_*` 解释层在 `ck_*` 观测层之上做架构判读；判读规则与 verdict 语义见 [depa context](../depa/index.md)。
- **llm-wiki**：把本图谱暴露为 MCP 工具、搜索与文档生成的产品面见 [llm-wiki context](../llm-wiki/index.md)。

| 类目 | 职责 | 何时阅读 |
|------|------|----------|
| [objects/](objects/index.md) | 事实关系与派生层对象的结构语义 | 想知道图里有什么、每列什么含义 |
| [policies/](policies/index.md) | 置信度分档、schema 演化、一致性等跨对象规则 | 要判断一条边可不可信、能不能改 schema |
| [workflows/](workflows/index.md) | 全量/增量索引与派生层物化流程 | 要理解图是怎么建出来、怎么保持新鲜的 |

## Code Map

- [code-map.md](code-map.md)
