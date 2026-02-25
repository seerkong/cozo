> 来源注记：复制自 ~/.claude/skills/depa-expert@2026-07-06（fa/rubrics/fact-grade-classification.md）。本项目真源（src/Om.Depa/rubrics/）优先演化，与全局 skill 双源各自演化——见 track expand-depa-detection-rules decisions.md。

# 核查表 — 把一个数据节点判进 7 级事实阶梯

> 给定**一个数据节点**（一个字段 / 一份文件 / 一个内存对象 / 一个缓存 / 一块 UI 状态），用一串 yes/no 问句把它定到 7 级阶梯之一。阶梯定义、特征、规则在 [`tao/fact-source-truth.md`](../../tao/fact-source-truth.md)——本核查表是它的"可勾流程"，不重复定义，只给判定路径与边界案例。
>
> 7 级 id（从高到低）：`authoritative_fact` / `domain_canonical_event` / `runtime_control_fact` / `append_only_journal` / `checkpoint_snapshot` / `derived_projection_cache` / `surface_view`。
>
> 被六步分析脉络的 ②（[`fa/method/02-fact-boundary.md`](../method/02-fact-boundary.md)）调用，定级结果落 `boundary/fact-grades.md`，带 `path:line`。

---

## 决策流程（顺序问，第一个"是"即定级）

每个节点**只能且必须**落进一级。按这个顺序问——顺序本身编码了"先排除呈现叶子、再排除派生、再分流水账/快照、最后区分控制面/事件/权威"。

```text
Q1  它只渲染、给人看，本身不持业务真相、不含状态机？
       是 → surface_view (7)          ←最末端叶子，先排除
       否 → Q2

Q2  它由上游事实按确定性规则算出、删了能从上游完全重建、不持久化为真相？
       是 → derived_projection_cache (6)
       否 → Q3

Q3  它是只追加、永不回改的流水账，且 live 主循环从不读它判断下一步？
       是 → append_only_journal (4)
       否 → Q4

Q4  它是在安全边界(safepoint)序列化出来、只在恢复/启动路径被读的快照？
       是 → checkpoint_snapshot (5)
       否 → Q5

Q5  它是调度/暂停/恢复/障碍这类"当前运行控制态"，由控制器在 live 直接置、
    且不能从 checkpoint/journal 推断？
       是 → runtime_control_fact (3)
       否 → Q6

Q6  它是跨边界显式发表、可重放、由 reducer 聚合而成的领域事件？
       是 → domain_canonical_event (2)
       否 → Q7

Q7  它有唯一写入者、活内存、无上游、live 直接改不经磁盘往返——它自己就是源？
       是 → authoritative_fact (1)
       否 → 定级不清：回 Q1 重看，或它根本不是关键节点
```

> 判完追加两道**交叉校验**（来自 [`tao/fact-source-truth.md`](../../tao/fact-source-truth.md) §2 五条规则）：
> - 唯一写入者是谁、是否唯一？多于一个 → 同时记入唯一写入者判定表的"混乱"行。
> - 数据图箭头方向：它只该被高级写、向低级供数。出现低级→高级的边 → 跑反写检查清单。

---

## 定级要点（每级一句"决定性特征"）

- **7 `surface_view`**：叶子、无下游、不含状态机；用户输入经显式 command 才进 live。
- **6 `derived_projection_cache`**：可完全重建、不反写上游、即便落盘也是缓存语义。
- **4 `append_only_journal`**：只 append、旁路、live 永不读它判断下一步、不影响 safepoint。
- **5 `checkpoint_snapshot`**：只在 safepoint 写、只在恢复/启动读、非 safepoint 保留 previous-known-good。
- **3 `runtime_control_fact`**：控制面当前态、live 直接置、不能从快照/流水账猜。
- **2 `domain_canonical_event`**：reducer 聚合、可重放、跨边界显式发表、发表后不可改。
- **1 `authoritative_fact`**：唯一写入者、活内存、无上游、不经磁盘往返。

最易混的两条分界线：
- **4 vs 5**：都落盘。问"它是给人看/replay 的旁路（4），还是用来崩溃恢复重建内存的（5）"。
- **1 vs 5**：内存权威是 1，它的磁盘影子是 5。**磁盘那份永远不是源**——任何"先写文件再回读来改内存"的路径是 5 gate 了 1，红灯。

---

## 边界案例（拿不准时对照）

### A. `history` 文件 —— 是 canonical event 还是 checkpoint？

看它**承载什么、被怎么读**：
- 若它是**只追加的领域事件序列**、可重放聚合出当前状态、跨边界发表的"发生过什么"——是 `domain_canonical_event (2)`（事件源本体，磁盘只是其载体）。
- 若它是**在 safepoint 把当前状态整体 dump 出来、用于崩溃后重建内存**——是 `checkpoint_snapshot (5)`。
- 判别问句：删掉它再从更上游重放能否得到等价状态？能且它本身是事件流 → 2；它是"某时刻状态的整体固化、不是事件流" → 5。
- 红灯变体：若 live 正常路径读 `history` 文件的末尾来决定"下一步做不做"——无论它是 2 还是 5，这条**读取路径**都违反规则③/④（把持久层当控制面用）。

### B. effects 日志 —— `append_only_journal`

副作用执行日志（"发出了哪些请求/写了哪些外部系统"）是典型 `append_only_journal (4)`：只追加、旁路、给审计/debug/replay 用。
- 达标信号：写它的代码不回喂主线；live loop 不读它分支。
- 红灯：出现"读 effects 日志第 N 条来判断要不要重发/要不要继续"——把 4 级当 1/3 级用，违反规则③。

### C. TUI 卡片 —— `derived_projection_cache`（呈现侧投影）

终端 UI 渲染的卡片/列表，从读模型算出来给人看：
- 它本身（渲染好的卡片数据结构）是 `derived_projection_cache (6)`——由事实投影而来、可重建、不持真相。
- 它**显示在屏幕上的那一层**是 `surface_view (7)`。实践中 6 与 7 常贴在一起：投影算出卡片(6)、终端把卡片画出来(7)。
- 红灯：卡片的选中态/本地编辑态**不经 command 直接改了业务主循环或某个 1/3 级事实**——UI 反向驱动，违反规则②/⑤。

### D. vm 快照泄漏 payload —— 反例（不是干净的 checkpoint）

一个本应是 `checkpoint_snapshot (5)` 的 vm/状态快照，**把单次调用的 input payload 一起序列化进去了**：
- 病象：快照里混入了本属 `input`（单次 payload）的数据（见 [`fa/rubrics/runtime-explicitness.md`](runtime-explicitness.md) 的 input/runtime 边界）。恢复时这份陈旧 payload 被当成状态重建，污染了 1 级权威。
- 为什么是反例：checkpoint 该只序列化 1–3 级权威/控制态，不该夹带单次 payload；payload 跨调用不复用、放进快照即制造"陈旧输入复活"。
- 定级处置：快照本体仍是 5 级，但标注"契约污染"——它把 input 混入了恢复状态。喂环节④收敛（拆出 payload，不入快照），归 [`violation-catalog.md`](violation-catalog.md) 的"事实源边界类 / 分层类"。

---

## 与脉络、tao、其他核查表的衔接

- 本核查表 = [`tao/fact-source-truth.md`](../../tao/fact-source-truth.md) 阶梯的"判定流程化"；定级标准、唯一真源五规则、反写检查清单都在那。
- 环节②用本流程给每个关键节点定级，落 `boundary/fact-grades.md`；混乱/反写转该文件 §3 判定表与 §4 红灯清单。
- 边界案例 D 的 input/快照污染与 [`runtime-explicitness.md`](runtime-explicitness.md) 互引；复发的反写模式经 [`shu/promote.md`](../../shu/promote.md) 晋升进 [`violation-catalog.md`](violation-catalog.md)。
