# 变更：trace 调用路径与 check 循环检测工具

## 背景和动机 (Context And Why)

mission `expand-llm-wiki-agent-tools`（P1）G3 第一弹。GitNexus 的 `trace`（A→B 调用路径，逐 hop file:line）与 `check`（import 循环）是 coding agent 理解"两个东西怎么连起来/哪里有环"的高频工具。P0 已备好地基：call_graph/import_graph 投影 + Cozo 内置 ShortestPathDijkstra/SCC 固定规则；DetectImportCyclesAsync 已有 internal 实现（G2 时代 ED-1 收敛，等待正式化）。

## "要做"和"不做" (Goals / Non-Goals)

**目标:**
- Om 查询 API（public，正式化）：
  - `TraceCallPathAsync(fromSymbolId, toSymbolId, options)` → 有序 hop 列表（symbol、name、file、line、via kind、confidence）；无路径返回空并说明；options：MaxDepth=16、MinConfidence=0.7、Direction（默认 down：from 调用方向）。
  - `DetectImportCyclesAsync` 正式化 public（+ 可选 CALLS 环检测参数）。
- 工具面（LlmWikiToolRunner 单实现三入口）：
  - `trace`：from/to 接受 symbol id 或名称（名称经消歧：唯一匹配直接用；多候选返回候选列表让 agent 选）；输出逐 hop 带 file:line。
  - `check`：cycles=import|calls|both（默认 import）；输出确定性环路径。
- smoke：真实图上 trace 已知路径（如 IndexAsync→PutEdgeAsync 链）与 check 空环/构造环。

**非目标:**
- 不做 k 条最短路（Yen）——留后续需要再说。
- 不做跨仓 trace。

## 变更内容（What Changes）

- Om.CodeKnowledge：trace API（ShortestPathDijkstra 固定规则 over call_graph）+ cycles 正式化。
- Tools：两个新工具注册与实现；Server 自动获得（MapTool 透传）。

## 影响范围（Impact）

- 受影响能力：cozo-dotnet-codeknowledge（新增 trace-and-cycles 需求）。
- 受影响代码：cozo-lib-dotnet/src/Om.CodeKnowledge/、llm-wiki packages/Tools、两侧 tests。
