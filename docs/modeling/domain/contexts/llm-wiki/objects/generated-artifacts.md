---
knowledge_plane: domain
doc_role: canonical
status: active
context: llm-wiki
last_verified: 2026-07-06
---

# 生成物：wiki 页面、分形骨架、skills、hook 注入块

llm-wiki 的四类生成物共享同一条身份原则：**生成物是图谱的确定性投影，不是新事实源**——同一图双跑输出一致，可随时重生成，人不改生成物本体而改上游（图或人工文档树）。

## wiki 页面（legacy）

`WikiCompiler` 产出的平铺 page-file-* 文档集：每页由 `ck_wiki_page` 计划（title + source_file_ids + symbol_ids + doc_ids）驱动。定位是兼容形态，新树以分形骨架为准。

## 分形骨架（fractal）

`FractalWikiPipeline` 产出的双分形文档树（modeling contexts + impl 六类目 + 根三件）：

- **context 发现**：以 `ck_community` 社群为 context 候选，受 MaxModelingContexts 等预算约束（按重要性排序截断：public-API 成员数降 → 总成员数降 → 名称升；超额丢弃进 diagnostics 并注明排序依据）。
- **受控格式**：frontmatter 精简 schema、目录职责块、index 导航表由 `FractalDocFormat` 单点渲染；`FractalSpecChecker` 机检产物对分形标准的符合度（0 违规为发布线）。
- **narrative 槽位**：语义正文段依赖 LLM；`--use-llm false` 时留 `_pending` 占位——**LLM 不可用是正常路径**（[policies/degradation-and-budget.md](../policies/degradation-and-budget.md)），骨架与结构事实照常产出。
- **增量**：页面输入哈希（FractalWikiMeta）不变则 skip，不重写未变页。
- **边界**：自举骨架供人工充实用底稿，**不直接覆盖**人工维护的 docs/ 真源树（本仓合并原则见 track add-docs-and-sop-skills findings）。

## skills

`SkillsGenerator` 从图谱生成 Claude Code skill 文件（per-context 导航 skill + exploring/impact/depa 等工作流 skill，附工具矩阵表）。确定性有界（deterministic-bounded 测试保障）；写盘侧所有权纪律见 [policies/ownership-safety.md](../policies/ownership-safety.md)。

## hook 注入块（additionalContext）

`hook augment` 对 Grep/Glob 结果反查 ck_symbol，注入 top callers/callees（confidence ≥ 0.7）与所在执行流的**有界**上下文块（字符预算默认 2000）。它是工具面价值的被动子集：agent 不调工具也能沾图谱的光。`hook staleness` 在 SessionStart 输出索引落后提示（比对 indexed commit 与 HEAD）。
