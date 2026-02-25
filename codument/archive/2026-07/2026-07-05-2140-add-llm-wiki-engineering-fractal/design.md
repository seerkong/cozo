# 方案设计：engineering 分形生成器

## 上下文

G2 产物：FractalWikiPipeline（四阶段、docs-preview、页级增量哈希、review 闸门、SanitizeNarrative）。验收夹具：analysis/fixtures.md（E-A 根结构…E-H 真源分离，30 条，[P]/[L] 分级）——本 track 的规格即该 checklist + 本文件。规范原文（生成器实现时可对照）：codument/std/skill/docs-engineering-fractal/index.md、std/spec/folder-manifest.md、std/attractors/model-driven-docs.md。

## 方案概览

1. **页面集扩展**（FractalWikiPipeline Phase2/3 重构为页面注册表驱动）
   - 根：index.md（导航+职责块完整型）、migration-map.md（生成台账：每次 build 记录页面增删改）。
   - impl/index.md（plane 完整型职责块）+ impl/global/{overview,howto,rules,examples,reference,troubleshooting}/index.md（精简型职责块 + 导航）。
   - overview/architecture.md：现 overview 页迁移（分组表+mermaid+叙述段）。
   - reference/code-map.md：现 code-map 迁移；reference/api-surface.md：public 符号表（exported+visibility=public，按组分节，零 LLM）。
   - howto/：从图确定候选主题（每个 top-N 组一页 "working-with-<group>.md"：组入口符号/关键流程步骤骨架 + llm 叙述段）；rules/：从约束型事实生成候选（import 环检测结果、社群跨界调用 top 列表作为"边界规则"骨架 + llm 段）；troubleshooting/：诊断类骨架（索引诊断分布 + llm 段）。数量预算：howto ≤10、rules/troubleshooting 各 1 起步页（记录取舍：主题页扩展依赖后续真实需求）。
   - examples/index.md：占位说明（何时填充，指向 howto）。
2. **职责块与 frontmatter**（internal FractalDocFormat 常量+构造器）
   - 精简型：`> 目录职责 · holds: … · excludes: … · tier: stable · ⬆from: … · ⬇to: …`（fixtures E-C 正则）。
   - frontmatter：knowledge_plane: impl（页面在 impl 下时省略？——规范说路径已表达 plane 时可省 knowledge_plane；按 fixtures 判定，保守输出 doc_role/status/last_verified 三字段 + modeling 特有字段禁用）。
   - index 只导航：正文（去链接列表）≤200 字。
3. **FractalSpecChecker**（Wiki 包 internal，InternalsVisibleTo 测试）：输入目录 → 逐条 [P] 断言（返回违规清单），供测试与 G5 验证复用。
4. **增量**：新页面全部进 .meta 哈希体系（structure/narrative 输入）；migration-map 追加式（不进哈希跳过判定或以内容哈希处理，取实现简单者记 findings）。
5. **dogfood**：本仓生成 → checker 全过 → [L] 项抽样评注入 findings。

## 影响范围与修改点（Impact）

- packages/Wiki（管线扩展 + FractalDocFormat + FractalSpecChecker）；tests。

## 决策摘要

- 单 global plane MVP；howto/rules 候选从图确定性生成（LLM 只叙述）；examples 占位（记录理由）。

## 风险 / 权衡

- 类目内容"骨架化"可能空洞 → 数量预算 + pending 叙述补写（G2 机制）+ [L] 评注如实记录。
- checklist 过严导致实现僵化 → checker 违规输出可读，逐条对应 fixtures 编号。

## 最小公开面

无新增 public 类型（FractalWikiOptions/Result 如需 add-only 字段允许）；FractalDocFormat/FractalSpecChecker internal。
