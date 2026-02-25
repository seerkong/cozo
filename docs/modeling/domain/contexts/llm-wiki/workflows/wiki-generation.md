---
knowledge_plane: domain
doc_role: canonical
status: active
context: llm-wiki
last_verified: 2026-07-06
---

# wiki 生成：legacy 与 codument-fractal 双管线

## Trigger

CLI 一级入口 `depa-wiki wiki --repo <仓根> --out <dir> [--pipeline legacy|codument-fractal]`，或 `build_wiki` 工具（`depa-wiki call build_wiki --work-dir <仓根> --output-directory <out>`）。`pipeline` 参数选管线（两个入口同一取值、同一分形执行路径）：

- `legacy`（默认）：`WikiCompiler` 平铺 page-file-* 文档。
- `codument-fractal`：`FractalWikiPipeline` 双分形骨架。结果 JSON 的 `pipeline` 字段统一回显 `codument-fractal`；未知取值直接报错列出合法值，不静默降级（track fix-wiki-fractal-entry-and-context-ranking T1.1，原 backlog W-1）。

## codument-fractal 管线步骤

1. **context 发现**：从 `ck_community` 社群构建 modeling context 候选（BuildModelingContexts），按预算（MaxModelingContexts、MaxWorkflowsPerContext）截断——MaxModelingContexts 按重要性排序（public-API 成员数降 → 总成员数降 → 名称升），超额丢弃记 diagnostics 并注明排序依据（track fix-wiki-fractal-entry-and-context-ranking T2.1，原 backlog W-2）。
2. **输入哈希与增量**：ComputePageInputs 为每页算输入哈希（FractalWikiMeta），与上次产物比对——未变页 skip，不重写。
3. **渲染**：BuildRenderers 按分形语法渲染根三件（index/migration-map/glossary）、modeling 树（contexts index / context index / code-map / objects / workflows）与 impl 树（plane/类目 index 与叶子），格式细节收敛在 FractalDocFormat（frontmatter 受控 schema + 目录职责块）。
4. **narrative**：LLM 可用时填充语义正文；不可用时留 `_pending` 占位（正常路径，[policies/degradation-and-budget.md](../policies/degradation-and-budget.md)）。
5. **自检**：`FractalSpecChecker.Check(输出, 仓根)` 机检产物对 [docs-modeling-fractal](../../../../../../codument/std/skill/docs-modeling-fractal/index.md) / [docs-engineering-fractal](../../../../../../codument/std/skill/docs-engineering-fractal/index.md) 标准的符合度，0 违规为发布线（M-D1 会校验 code-map 路径真实存在）。

## Postconditions & 边界

- 输出树跨进程重跑确定一致（E2E 测试保障）。
- 骨架是**底稿**：Boundary/Not Owned Here 等语义句与类目正交性仍需人工判断，合并进人工 docs/ 树时按"只裁剪素材、不整树覆盖"原则执行。
- 操作入口与参数见用户手册 [skills-and-wiki.md](../../../../../../cozo-lib-dotnet-llm-wiki/docs/skills-and-wiki.md)。
