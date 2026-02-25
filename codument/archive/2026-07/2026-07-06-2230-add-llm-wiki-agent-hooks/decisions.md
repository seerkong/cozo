# Decisions

## Usage
- 执行期决策追加至此

### 1. 【P1】富化范围与挂载点
- augment 只富化 Grep|Glob；staleness 挂 SessionStart。状态：decided（design 决策摘要）

### 2. 【P1】PreToolUse→PostToolUse 收敛（mission 任务名偏差）
- mission 任务名写 PreToolUse 图富化；Claude Code 协议 tool_response 仅 PostToolUse 可得，收敛为 PostToolUse 富化（语义等价 GitNexus native_augment）。mission reports/track-bind-002.md 同步记录。状态：decided
