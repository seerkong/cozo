# 变更：agent 工具面端到端验证（P1 mission 收口）

## 背景和动机 (Context And Why)

mission `expand-llm-wiki-agent-tools` G5 收口 track。G2–G4 已交付：混合检索、trace/check、context/impact 深图化（含缺口①③修复）、detect_changes/staleness。本 track 做独立端到端验证 + agent 实测（dogfood），对 mission 成功判据逐条取证。

## "要做"和"不做" (Goals / Non-Goals)

**目标:**
- 真实仓 E2E（sqlite 引擎，本仓 dogfood）：索引 cozo-lib-dotnet/src 后走完整 agent 工作流脚本——semantic_search（精确名 + 中文 + 概念查询三类）→ symbol_context（富化字段）→ trace（真实链）→ impact_of_change（direction/risk）→ 改动一处 → detect_changes（映射+stale）→ check。每步输出人工评注可用性。
- 混合搜索质量取证：10 个样例查询（5 精确标识符 + 5 概念自然语言）对比 hybrid vs vector-only 的目标命中排名（记录数字，展示改进）。
- 15 工具 smoke 全量回归 + 两侧测试全绿。
- 汇总 reports/verify-report.md：mission 成功判据逐条证据 + 遗留问题清单（交接 P2/P3）。

**非目标:**
- 不新增功能（验证中小 bug 可修，记录）。
- 不做性能基准（记录耗时即可）。

## 变更内容（What Changes）

- 验证脚本/探针（/tmp）+ 可沉淀的 smoke 追加；行为契约：llm-wiki-tools 新增 agent-tools-e2e-quality 需求。

## 影响范围（Impact）

- 受影响能力：llm-wiki-tools。
- 受影响代码：tests（可能追加）；不动 packages 主体。
