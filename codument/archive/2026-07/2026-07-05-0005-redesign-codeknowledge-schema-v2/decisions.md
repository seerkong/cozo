# Decisions

## Usage
- 本 track 的关键决策已在 mission 层由用户确认（D1–D5，见 missions/active/deepen-llm-wiki-code-graph/decisions.md）
- 规划期无新增待确认决策；执行期新决策追加至此

## 执行期决策

### ED-1 固定规则消费 API internal 化（2026-07-04，AttractorCheck GAP-1）
- 决定：`RunCommunityDetectionAsync`/`DetectImportCyclesAsync` 及其结果 records（`CodeCommunitySummary`/`CommunityDetectionResult`/`ImportCycle`）改为 internal，实现保留；测试通过 InternalsVisibleTo(Cozo.DotNet.Om.Tests) 继续覆盖。投影常量 `CodeGraphProjections` 保持 public（design.md §5 范围内）。
- 理由：proposal.md 非目标明确社群检测/执行流计算归 G5 track（本 track 只建表）；占位语义（label=最高度数符号名、cohesion=0.0）不应进入公开契约，G5 正式化时可自由改签名后再升为公开 API。
