# Mission Design：harden-llm-wiki-engineering

## 控制论模型

- desired state：mission.xml DAG（增量索引 → hooks →（并行）skills / eval / 配置 / 多仓库），叶子 `cdt:TrackLink`。
- actual state：P0/P1/P2 mission 产出、索引性能基线、hook 装配状态、eval 报告。
- actuation：创建/续跑/归档 track；受控 replan。
- feedback：性能数据、agent 会话观察、eval 对比报告。

## Mission Actors

| Actor | 控制论角色 | DEPA 归属 | 职责 |
|---|---|---|---|
| MissionPlanner | 期望态产出者 | Processor + Actor | 产出/修订 desired graph |
| MissionObserver | 传感器 | Data + Actor | 读取性能/装配/eval actual state |
| MissionReconciler | 控制器 | Processor + Actor | 判定 drift / ready / blocked |
| MissionApplier | 执行器 | Effect + Actor | bounded action |

## plan vs track

mission 只做控制面；增量索引、hook 脚本、skills 生成器、eval harness 由 track 落地。

## 关键设计约束

1. **按需启动**：本 mission 各 TaskGroup 相对独立，允许只启动部分（如先 hooks + 增量），其余 SUPERSEDED/延后——收口不要求全绿。
2. **增量正确性优先**：增量结果必须与全量一致（抽样 diff 校验门禁），性能第二。
3. **hook 非侵入**：hook 失败必须静默降级，不阻塞 agent 正常工作。
4. **eval 从小做起**：先自建 10–20 个本仓任务的对比集，验证方法论后再考虑 SWE-bench。

## 决策 frontier（进入 active 时收敛）

- 增量粒度：文件级 vs 符号级（GitNexus 也还在文件级 + 规划符号级）。
- hook 富化的注入格式与 token 预算。
- eval 的 agent harness 选型。

## 风险

| 风险 | 缓解 |
|---|---|
| 增量与全量结果漂移 | 一致性抽样校验为硬门禁 |
| hook 注入噪声反而降低 agent 表现 | eval 基准先行，量化后再默认开启 |
| P3 过早启动分散主线 | 保持 pending，P0/P1 收口后由用户显式启动 |
