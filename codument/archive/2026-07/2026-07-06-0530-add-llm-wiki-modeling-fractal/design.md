# 方案设计：modeling 分形生成

## 上下文

G3 产物：FractalWikiPipeline（页面注册表 + structureHash/narrativeInputsHash 增量 + review 稿闸门 + FractalDocFormat）、FractalSpecChecker（E-* 23 条，M-* 20 条 NotImplementedRuleIds）。规范：docs-modeling-fractal/index.md（plane→context→类目→叶子；domain 类目 objects/policies/workflows；index 只导航；frontmatter 加 context 字段；derived 填 derived_from——本 track 只出 canonical 不涉 derived）。fixtures 的 M-* 20 条为验收。

## 方案概览

1. **context 发现**（确定性 + 审查稿）
   - 候选 = 社群成员的限定名（sym_key 去 lang/arity）最长公共点分前缀；无前缀（跨根社群）→ 该社群按顶层命名空间段拆分归并到相应 context；仍无 → 最高加权度类型名作 context 名（禁 #N）。
   - 同前缀多社群合并为一个 context；context slug = 前缀小写点转中横线。
   - LLM 归组审查稿：沿用 G2 grouping-review 机制（merge/rename/flag，不进缓存键）；离线跳过。
   - 上限：MaxContexts 默认 24（size 降序截断入 misc-context？不——截断丢弃并记 migration-map/诊断，与 A2 口径一致记 findings）。
2. **页面注册表扩展**（并入既有体系）
   - `modeling/index.md`（导航+职责块）、`modeling/domain/index.md`、`modeling/domain/contexts/index.md`、每 context：`index.md`、`code-map.md`、`objects/index.md`、`objects/<type-slug>.md`（该 context 内 public 类型，预算 MaxObjectsPerContext=30 截断计数）、`workflows/index.md`、`workflows/<process-slug>.md`（入口符号属于该 context 的流，预算 10/context）。policies 不建（决策记录）。
   - frontmatter：knowledge_plane 由路径表达、doc_role=canonical、context=<name>、status/last_verified 同 impl 口径。
   - objects 叶子结构层：类型 kind/signature、成员表（name/kind/line）、EXTENDS/IMPLEMENTS 关系、参与执行流（≤5）、file:line 链接；叙述槽 LLM。
   - workflows 叶子：入口/kind、步骤表（step/symbol/file:line）、cross_community 标注；叙述槽。
3. **checker M-* 补齐**：实现 fixtures M-* 20 条（context 层结构、类目、frontmatter context 字段、canonical 单真源、code-map 存在性等以 fixtures 原文为准）；每条配 discrimination mutation。
4. **dogfood**：本仓双分形一次生成（impl+modeling），checker E-*+M-* 全集 0 违规；二跑全 skip；[L] 抽样评注（context 命名可解释性重点）。

## 影响范围与修改点（Impact）

- FractalWikiPipeline.cs（modeling 平面注册表/渲染/context 发现）、FractalSpecChecker.cs（M-*）、FractalDocFormat 复用；tests。

## 决策摘要

- policies 类目不建（空类目规则 + 无证据源）；derived plane 非目标。
- context 截断丢弃 + 计数（不造 misc 容器 context）。

## 风险 / 权衡

- context 命名质量 → 三级兜底（前缀/命名空间段/类型名）+ 审查稿；禁 #N 是硬规则（delta case）。
- 页面量膨胀 → per-context 预算 + MaxContexts。

## 最小公开面

零新增 public 类型（全部 internal，延续 G3 口径）；FractalWikiOptions 如需开关用 add-only 字段。
