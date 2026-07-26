# skill: codument-impl-mission（执行长周期 mission）

按 `mission.xml` 的 desired DAG 执行 mission，并通过 `MissionPlanner` / `MissionObserver` / `MissionReconciler` / `MissionApplier` 四个控制论 + DEPA actor 做反馈收敛。

> mission 执行不是一次性按 DAG 跑完。它是 level-triggered 的控制循环：每轮读取当前 actual state，比较 desired state，只执行一个 bounded convergence action。格式规范见 `codument/std/spec/mission-xml-spec.md`；flow notation 见 `codument/std/spec/flow-notation.md`。

## 0. 前置

- mission 必须位于 `codument/missions/pending/<id>/` 或 `codument/missions/active/<id>/`。
- `mission.xml` 是状态真源。
- `analysis/` 和 `reports/` 是执行期外部记忆，默认不进 git。
- 不依赖 chat history 作为状态。

## 1. ActorSet 与 session runtime

标准四 actor 协议、ActorSet 继承和 canonical examples 只由 `std/spec/mission-xml-spec.md` 定义。本 action 根据当前 TaskGroup 选择最近的完整 ActorSet，并执行其中 `<Description>` 所声明的本 mission 工作方式；不在这里复制角色定义。

执行 session 以临时 `WorkspaceBinding` 提供 `ProjectRef -> workspace root` 映射。它不是配置、XML 节点或 report 内容，绝不写入 mission、track、report、decision 或任何可提交文件：

- 没有 external ProjectRef binding 时，Observer 报告 `UNBOUND`。这只是本次观察结果，不是持久 status。
- binding 存在但目标项目找不到 TrackLink 指向的 `track.xml` 时，报告 `MISSING`，并以目标项目自己的 `track.xml` 作为实际态真源。
- Reconciler 只阻断直接依赖该 ProjectRef 的 action；其他 ready DAG branch 仍可执行。

## 2. 主循环

### 2.1 用户明确要求实现 / 落地时的连续执行边界

如果用户话语明确包含“实现 mission”、“落地 mission”、“执行整个 mission”、“continue until done”等含义，`pending` 启动只是第一个 bounded action gate，不是本次请求的终点。

在这种场景下，完成 `start-mission` 后必须重新读取 `active/<id>/mission.xml`，继续进入本主循环，直到出现以下任一状态：

- mission 满足 completed gate 并被更新为 `completed`。
- mission `cancelled` / `superseded`。
- 遇到真实 `BLOCKED`：缺少用户决策、外部输入、失败 track 或无法自动修复的结构偏差。
- 用户显式限制本轮只做启动、只做一个节点、只做某个 track。

仍然保持“每轮只执行一个 bounded action，动作后重新 observe / reconcile”的节奏；禁止把 `start-mission` 的完成当作“已实现 mission”的最终响应。

```text
@delimiter: --
@node: #
@marker: ?
-- #loop ?mission until="mission completed/cancelled/superseded or blocked"
---- #step ?load
定位 mission：优先读取 active/<id>/；若只存在 pending/<id>/，本轮唯一允许的 bounded action 是 start-mission（见 §3），不得执行任何 ready node。
---- /?load
---- #if ?pending_start cond="mission 位于 pending/<id>/"
------ #call ?start target="start-mission(pending/<id>)"
------ /?start
------ #exit ?reload_active
如果用户只要求 start，则启动完成后停手；如果用户要求 implement / 落地 / 执行，则从 active/<id>/ 重新加载继续主循环。不要沿用 pending 路径缓存继续执行 DAG。
------ /?reload_active
---- /?pending_start
---- #step ?observe
MissionObserver 读取 actual state：mission.xml 当前状态、根据 session WorkspaceBinding 定位各 `cdt:TrackLink project-ref` 的真实 tracks、archive、测试结果、用户新约束、reports。对未绑定外部项目投影 UNBOUND；不得把 workspace path 写回文件。
---- /?observe
---- #step ?reconcile
MissionReconciler 比较 desired vs actual，判定：ready-node / drift / blocked / completed。
---- /?reconcile
---- #switch ?decision on="reconcile result"
------ #case ?ready when="存在 ready mission node"
-------- #step ?apply_ready
MissionApplier 执行一个 bounded action：分析一个 plan 节点、创建一个 track、续跑一个 track、归档一个 track，或写一个报告。
-------- /?apply_ready
-------- #exit ?wait_after_action
动作完成后停手或进入下一轮；不要无界连续执行多个 track。
-------- /?wait_after_action
------ /?ready
------ #case ?drift when="actual state 使当前 DAG/节点不再成立"
-------- #step ?plan_revision
MissionPlanner 基于 evidence 或 human decision 产出重规划建议。
-------- /?plan_revision
-------- #step ?apply_replan
MissionApplier 写 reports/replan-XXX.md，更新 mission.xml，递增 Revision。
-------- /?apply_replan
-------- #exit ?wait_replan
重规划后停手，等待下一轮观察。
-------- /?wait_replan
------ /?drift
------ #case ?blocked when="缺少 evidence、用户决策、外部状态或 track 失败"
-------- #return ?blocked_out value="报告阻塞和所需输入"
-------- /?blocked_out
------ /?blocked
------ #case ?done when="全部节点 DONE 或 SUPERSEDED，成功判据满足"
-------- #return ?complete value="更新 mission.xml Status=completed；提示可 archive-mission"
-------- /?complete
------ /?done
---- /?decision
-- /?mission
```

## 3. 启动 pending mission

当 mission 位于 `pending/<id>/`：

`start-mission` 是一个门禁动作，不是普通 ready node。完成前不得执行 mission DAG 中的任何节点。

1. 读取 `proposal.md`、`design.md` 和 `mission.xml`。
2. 确认用户要启动；如果用户未明确启动，返回 blocked。
3. 检查 `active/<id>/` 不存在；若已存在，返回 blocked，要求人工处理，禁止覆盖。
4. 移动目录到 `active/<id>/`。
5. 更新 `active/<id>/mission.xml`：
   - `Metadata.Status=active`
   - `Metadata.UpdatedAt=<now>`
6. 写 `reports/mission-run-001.md` 记录启动路径、启动时间和用户确认。
7. 如果用户只要求 start，启动动作完成后停手；如果用户要求 implement / 落地 / 执行整个 mission，必须从 `active/<id>/` 重新加载再进入下一轮观察；禁止继续使用旧的 `pending/<id>/` 路径引用。

## 4. ready node 处理

ready node 来自 `mission.xml` 顶层 `TaskGroup` DAG：所有 `<After>` 前驱已 DONE / SUPERSEDED，且节点自身未完成。进入某个 ready `TaskGroup` 后，按其内部叶子 `Task` 的 `order` 顺序执行第一个未完成 Task；除非未来显式扩展 nested DAG，否则组内 Task 不并行、不写进顶层 DAG。

常见节点类型：

- 普通 leaf `Task`：做证据盘点 / 设计收敛 / track 切片；产物写 `analysis/`，稳定结论写 `design.md` 或 decisions。
- 带 `cdt:TrackLink` 的 leaf `Task`：创建、续跑、验证或归档一个 codument track；真实实现交 `codument-plan-track` / `codument-impl-track` / `codument-archive-track`。
- 验证 leaf `Task`：独立验证 mission 成功判据。

### 4.1 TrackLink 绑定写回

`cdt:TrackLink` 只挂在叶子 `Task` 上，并显式指向其 ProjectRef：

```xml
<Task id="G3-T1" name="创建并执行 runtime contracts track" status="NOT_STARTED" order="0">
  <cdt:TrackLink state="candidate" id="add-runtime-contracts" project-ref="host"/>
</Task>
```

TrackLink 是对真实 track 生命周期的承诺，不是一个普通标签：

- `state="candidate"` 只表示推荐 track id，不能代表 track 已存在。
- `state="bound"` 只能在 ProjectRef 的 session WorkspaceBinding 可解析且目标项目中真实 track 可解析后写入：`codument/tracks/active/<id>/track.xml` 存在，或 `codument/tracks/archived/**/<timestamp>-<id>/track.xml` 存在。
- 带 `cdt:TrackLink` 的 ready leaf 的合法动作是：创建 track、绑定 TrackLink、执行 / 验证 / 归档该真实 track。
- 直接改代码而不创建真实 track 是非法动作；如果执行中确认该叶子不应再由 track 承担，必须先受控重规划，supersede / 移除该 `TrackLink`，并在 replan report 中记录原因。

当 `MissionApplier` 创建真实 track 后，必须立即更新 `mission.xml`：

1. 通过 session WorkspaceBinding 定位 TrackLink 的 `project-ref`，再验证该目标项目中真实 track 存在：`codument/tracks/active/<id>/track.xml`，或 `codument/tracks/archived/**/<timestamp>-<id>/track.xml`。
2. 将同一个 `cdt:TrackLink` 的 `state` 从 `candidate` 改为 `bound`。
3. 将 `id` 写成真实 track id；如果真实 id 与 candidate id 不同，用真实 id 覆盖。
4. 不写 `path`、`archive-path`、workspace 或 track 状态；active/archive 位置只在本 session 通过 ProjectRef binding 与 id 解析。
5. 更新该 leaf `Task.status`，并更新 `Metadata.Revision` / `UpdatedAt`。
6. 写 `reports/track-bind-XXX.md`，至少包含 mission task id、candidate id、real track id、创建证据和时间。

如果 `cdt:TrackLink state="candidate"` 指向的 track 已经存在，也按同样规则绑定为 `bound` 并写 report；不得重复创建 track。

## 5. 受控重规划

允许变更：

- 增加 mission 节点。
- 删除 / supersede mission 节点。
- 修改节点描述、验收、状态。
- 修改 DAG 依赖。
- 改变某 `cdt:TrackLink state="candidate"` 的边界、id 或顺序。

硬要求：

- 必须有 evidence 或 human decision。
- 必须写 `reports/replan-XXX.md` 或 `reports/human-intervention-XXX.md`。
- 必须递增 `Metadata.Revision`。
- 必须更新 `Metadata.UpdatedAt`。

## 6. reports 模板

```markdown
# Mission Replan Report

## Trigger

## Desired State

## Actual State

## Diff

## Decision

## Applied Change

## Next Observation
```

## 7. 完成

当所有必要节点 DONE 或 SUPERSEDED，且 proposal 的成功判据满足：

- 先执行 completed gate（见 §7.1）。gate 未通过时不得更新为 `completed`。
- 更新 `mission.xml` status 为 `completed`。
- 写 `reports/mission-complete.md`。
- 提示用户使用 `codument-archive-mission` 归档。

### 7.1 Completed gate

更新 `Metadata.Status=completed` 前必须逐项确认：

- 所有必要 `TaskGroup` / `Task` 都是 `DONE` 或 `SUPERSEDED`；`SUPERSEDED` 必须有 replan / human-intervention report 解释。
- 所有 `cdt:TrackLink state="bound"` 都能通过当前 session 的 ProjectRef binding 解析到目标项目的真实 track：`codument/tracks/active/<id>/track.xml` 或 `codument/tracks/archived/**/<timestamp>-<id>/track.xml`。
- 已完成任务上不应残留 `cdt:TrackLink state="candidate"`；除非该任务被 `SUPERSEDED`，且 report 说明该 candidate 被取消或改由其他节点承担。
- 所有关联的真实 track 已完成、归档，或有明确 superseded / abandoned 证据；不能只凭 mission report 声称已完成。
- `proposal.md` 的成功判据都有证据：track 验证报告、测试结果、代码位置、设计决策或人工确认。
- mission reports 不自相矛盾：例如不得同时写“未创建 track”与 `TrackLink state="bound"`，或写“已完成”但缺少对应 track。
- `mission.xml` XML 格式有效；对每个 linked track，best-effort 运行 `codument validate <track-id> --strict` 或项目当前等价校验。

任一项失败时，不得标记 `completed`。应进入 drift / replan / blocked 分支，先修复结构偏差或向用户报告阻塞。
