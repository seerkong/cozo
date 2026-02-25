# 变更：愿景层端到端验证（P2 mission 收口）

## 背景和动机 (Context And Why)

mission `add-llm-wiki-depa-fractal-wiki` G7 收口 track。G2–G6 已交付：LLM wiki 管线（多后端+增量+review 闸门）、engineering 分形（E-* 23 条检查）、modeling 分形（M-* 全集、context 发现禁 #N）、DEPA 本体与观测层、8 条红灯检测与三工具。本 track 做独立端到端验证，对 mission proposal 三条成功判据逐条取证，并固化质量门禁为行为契约。

## "要做"和"不做" (Goals / Non-Goals)

**目标:**
- 双分形 E2E：本仓真实索引 → 双分形生成（离线确定性）→ FractalSpecChecker E-*+M-* 全集 0 违规；跨进程双跑一致；增量（改一个源文件只重建受影响页）。
- DEPA E2E：合规/违规双样本经 depa_conformance 工具输出可区分结论（复核 G6 证据 + 独立重跑）；本仓真实 dogfood 报告结构合理（六维、BLOCKED 诚实）。
- 工具面回归：18 工具 smoke 全过；两侧测试全绿。
- 汇总验证报告 reports/verify-report.md：mission 三条成功判据逐条（判据原文 → 证据 → PASS/FAIL）；发现的问题如实记录。

**非目标:**
- 不新增功能（验证中发现的小 bug 修复除外，需记录）。
- 不做性能基准化。

## 变更内容（What Changes）

- 行为契约：llm-wiki-pipeline 新增 vision-e2e-quality 门禁需求。
- 可能的持久 smoke 追加；其余留探针。

## 影响范围（Impact）

- 受影响能力：llm-wiki-pipeline。
- 受影响代码：tests（可能追加）；不动 packages 主体。
