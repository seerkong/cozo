# 变更：语义代码图端到端验证（mission 收口）

## 背景和动机 (Context And Why)

mission `deepen-llm-wiki-code-graph` G6 收口 track。G2–G5 已交付：v2 schema、tree-sitter+Roslyn 混合解析、CALLS/ACCESSES/OVERRIDES、社群、执行流。本 track 做独立端到端验证，对 mission proposal 的成功判据逐条取证，并把质量门禁固化为行为契约。

## "要做"和"不做" (Goals / Non-Goals)

**目标:**
- 双仓 E2E：cozo-lib-dotnet（C#）与 cozo-lib-dotnet-llm-wiki-viz（TS）全量索引（真实 sqlite 引擎），全链路（符号→边→社群→执行流）落库可查。
- 调用边质量正式门禁：conf≥0.8 的 CALLS 边随机抽 30 条人工核对，正确率 ≥90%（mission 门禁）。
- 派生层有界性与可解释性：社群/执行流数量在预算内，label/步骤抽样评注。
- 12 个 MCP 工具全量回归 + symbol_context/impact_of_change 相对正则时代的提升取证（对照 G2 前基线行为描述）。
- 汇总验证报告（track reports/verify-report.md），作为 mission-complete 的证据源。

**非目标:**
- 不新增功能代码（验证中发现的 bug 修复除外，需记录）。
- 不做性能基准化（记录耗时即可）。

## 变更内容（What Changes）

- 验证脚本/断言（可作为持久 smoke 追加进测试套件的部分追加，其余留探针）。
- 行为契约：cozo-dotnet-codeknowledge 新增 code-graph-quality 需求（固化门禁口径）。

## 影响范围（Impact）

- 受影响能力：cozo-dotnet-codeknowledge。
- 受影响代码：tests（可能追加持久断言）；不动 packages 主体。
