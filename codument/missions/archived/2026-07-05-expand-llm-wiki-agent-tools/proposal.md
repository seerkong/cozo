# Mission：expand-llm-wiki-agent-tools（P1 · coding-agent MCP 工具面补齐）

## 背景和动机

2026-07-04 GitNexus gap 分析：GitNexus 面向 coding agent 暴露 16 工具 + 7 资源（`gitnexus/src/mcp/tools.ts`），cozo-wiki 现有 12 工具。逐项对比后，cozo-wiki 缺失且对 agent 价值最高的能力都消费"深图"（P0 mission `deepen-llm-wiki-code-graph` 的产出）：

| GitNexus 工具 | 能力 | cozo-wiki 现状 |
|---|---|---|
| `trace` | A→B 最短调用路径，逐 hop 带 file:line、边类型、confidence | 无 |
| `detect_changes` | git diff（unstaged/staged/compare）→ 改动行映射符号 → 受影响执行流 → 风险评级 | 无任何 git 集成 |
| `context` | 符号 360°：定义 + 传入/传出调用分类 + 继承 + 执行流参与度 | symbol_context 雏形，无调用分类/流参与 |
| `impact` | 按深度分层（d=1 直接/d=2 间接/d=3 传递）+ 分页 + minConfidence 过滤 + 风险评级（LOW→CRITICAL） | impact_of_change 仅 import 级 |
| `check` | import 循环检测（确定性路径） | 无 |
| `rename` | graph（高置信）+ text_search（低置信）分级重命名建议，dry-run | 无 |
| 混合搜索 | BM25 FTS + 向量 RRF 融合（K=60），结果按 Process 聚合 | 仅纯向量（HNSW） |
| staleness | 索引 lastCommit vs HEAD，提示 agent 重新索引 | 无 |

CozoDB 自带 FTS 索引与 Datalog 递归，混合搜索与路径/环检测在 Cozo 侧实现成本低——这是差异化优势。

## 目标

1. 混合搜索：Cozo FTS(BM25) + 现有 HNSW 向量 + RRF 融合，semantic_search 升级。
2. `trace`（调用路径）、`check`（import/调用循环检测）。
3. `detect_changes`：git diff → 符号映射 → 影响 → 风险评级；staleness 提示。
4. `symbol_context` / `impact_of_change` 深图化：调用方向分类、深度分层、confidence 过滤、执行流参与、风险评级。
5. `rename` 建议工具（graph + text 分级，dry-run 默认）。
6. 所有新工具经 MCP/CLI/HTTP 三入口（复用 LlmWikiToolRunner 单实现模式）。

## 非目标

- 不做框架感知工具（route_map/api_impact/shape_check）——待真实需求。
- 不暴露 raw CozoScript endpoint（沿用安全折衷，走 Portable Datalog / query_named）。
- 不做 PDG 类工具。

## 成功判据

- 新工具在 cozo 仓库自身上可用且有 smoke；`trace`/`detect_changes` 输出带 file:line 可点击定位。
- 混合搜索相对纯向量在样例查询集上有可展示的改进。
- 前置依赖：P0 mission 的 CALLS/confidence 边可用；本 mission 各 track 不得绕过 P0 自造浅图逻辑。

## 为什么是 mission 而不是单 track

6 类独立可验证的工具能力、跨 Tools/Server/McpServer 多包、依赖 P0 产出的阶段门控，需要分批 track 落地与阶段验证。
