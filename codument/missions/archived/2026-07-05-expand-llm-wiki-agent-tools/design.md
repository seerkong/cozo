# Mission Design：expand-llm-wiki-agent-tools

## 控制论模型

- desired state：mission.xml DAG（契约收敛 → 搜索/图查询工具 → git 集成工具 → 验证），叶子 `cdt:TrackLink`。
- actual state：P0 mission（deepen-llm-wiki-code-graph）产出状态、tracks/archive、LlmWikiToolRunner 工具面现状、tests。
- actuation：创建/续跑/归档 track；受控 replan。
- feedback：track reports、smoke、agent 实测（本仓库 dogfood）。

## Mission Actors

| Actor | 控制论角色 | DEPA 归属 | 职责 |
|---|---|---|---|
| MissionPlanner | 期望态产出者 | Processor + Actor | 产出/修订 desired graph |
| MissionObserver | 传感器 | Data + Actor | 读取 P0 依赖与工具面 actual state |
| MissionReconciler | 控制器 | Processor + Actor | 判定 drift / ready / blocked（尤其 P0 前置门） |
| MissionApplier | 执行器 | Effect + Actor | bounded action |

## plan vs track

mission 只做控制面；工具实现/git 集成/搜索融合由 track 承担。

## 关键设计约束

1. **单实现三入口**：新工具一律进 LlmWikiToolRunner，MCP/CLI/HTTP 自动获得（extract-llm-wiki-tool-runner track 确立的模式）。
2. **P0 硬前置**：trace/impact/context 深图化依赖 CALLS+confidence 边；P0 未收口前相关 TaskGroup blocked。
3. **Cozo 优先**：路径查询/环检测/RRF 优先用 Datalog 递归与 Cozo FTS 表达，减少 .NET 侧图算法。
4. **分页与上限**：所有图工具带 limit/offset 与深度上限（GitNexus：impact maxDepth 32、RRF K=60 可作初始参数）。
5. **git 集成隔离**：git diff 解析作为独立 Effect capsule（契约 + 实现），不散落在工具逻辑里。

## 决策 frontier（进入 active 时收敛）

- 混合搜索 RRF 参数与 FTS 分词（中文注释场景）。
- detect_changes 的 diff 范围语义（unstaged/staged/compare base）与 worktree 支持。
- rename 是否 MVP 或后置。

## 受控重规划 / 人工介入 / 风险

- P0 图质量不达标 → 本 mission 相关 track 暂停并回馈 P0（写 replan 报告）。
- 风险：工具面膨胀稀释质量 → 每工具必须带 smoke + 文档 + agent 实测记录；宁缺毋滥。
