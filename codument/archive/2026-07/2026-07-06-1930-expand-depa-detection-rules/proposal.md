# 变更：DEPA 检测规则扩展与判据真源落位

## 背景和动机 (Context And Why)

2026-07-06 对比分析（analysis/gap-matrix.md，规划输入真源）：depa-expert skill 判据全集 vs Om.Depa 工具——已覆盖 12 / 静态可补 16 / 应占位 11（现仅 3）/ 仅人工 4，并发现 dimension 口径错位与 skill 值得反向吸收的 4 条工程化口径。用户裁定（2026-07-06 三问确认）：全部 16 条静态可补都做；判据文件复制进项目作真源（自包含）；改写全局 depa-expert skill 吸收工具侧口径。

## "要做"和"不做" (Goals / Non-Goals)

**目标:**
1. **判据真源落位**：depa-expert 的 rubrics（violation-catalog + 5 张核查表）与 fact-source-truth 关键节复制到 `cozo-lib-dotnet/src/Om.Depa/rubrics/`，并新增 `rule-map.md`（工具规则 id ↔ catalog 条目 ↔ 实现状态三方映射表，含"仅人工 4 条"标注）；工具/设计文档的 ~/.claude 引用改指项目内真源。
2. **口径修正**：V-F1/V-F2 dimension 按 rubrics 原文重裁（记录决策）；DepaOntologySchema dimension 词表补 actor；报告分组扩为 8 组（+overdesign/vendor）。
3. **占位补全**：C2/C3/G1/G3/vendor 组 BLOCKED 占位（reason 命名缺失），报告 coverage 分母诚实化到全目录口径。
4. **检测器批次一（14 条，零新标注）**：V-L4/V-L2/V-F3/V-G1/V-E3/V-D3/V-R1/V-C1/V-L5/V-L6/V-S1 扩展/V-S3/V-A1/V-S4（判定思路见 gap-matrix 批次一表）。
5. **检测器批次二（3 条，depa-map add-only 新键）**：recoveryPaths → V-S2a/b；layers → V-P2。
6. **复扫 dogfood**：本仓全规则复扫（新规则命中如实分析真伪，误报走标注或阈值调优，不为清零硬压）。
7. **全局 skill 反哺**（仓外改动，findings 记录 diff 摘要）：~/.claude/skills/depa-expert 吸收 4 条口径 + 工具辅助观测路径。

**非目标:**
- 静态不可判 11 条不做检测器（占位诚实呈现）；仅人工 4 条不进工具。
- 不改 8 条既有检测器的判定语义（仅 dimension 归属修正）。

## 变更内容（What Changes）

- Om.Depa：rubrics/ 目录、检测器 8→~25、占位词表、报告 8 组、depa-map 新键（recoveryPaths/layers，add-only）。
- 全局 skill（仓外）：protocols/rubrics 增补。
- tests：每条新规则正反 case。

## 影响范围（Impact）

- 受影响能力：cozo-dotnet-codeknowledge（新增 depa-extended-rules 需求）。
- 受影响代码：cozo-lib-dotnet/src/Om.Depa/、tests；仓外 ~/.claude/skills/depa-expert（记录性改动）。
