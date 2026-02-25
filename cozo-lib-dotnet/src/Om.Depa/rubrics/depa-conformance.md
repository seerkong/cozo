> 来源注记：复制自 ~/.claude/skills/depa-expert@2026-07-06（fa/rubrics/depa-conformance.md）。本项目真源（src/Om.Depa/rubrics/）优先演化，与全局 skill 双源各自演化——见 track expand-depa-detection-rules decisions.md。

# 核查表 — DEPA 四维 7 点审查清单

> 给一段代码（一个模块 / handler / agent / 子系统）逐项打勾，得出 DEPA 符合度。每项是一个**问句**：能明确回答"是"才算这一点符合。论断带 `path:line` 证据（观测优先，见 [`fa/method/06-cross-cutting-principles.md`](../method/06-cross-cutting-principles.md)）。
>
> 本核查表是判据的"勾选项"；每点的"为什么这是对的"在对应 `tao/dimension-*.md`。主要被六步分析脉络的 ③④ 反复调用。

---

## 怎么用

1. 锁定评审对象（一段逻辑 / 一个模块）。
2. 逐项问句作答，给 `符合 | 部分 | 违反`，每个非"符合"附 `path:line` 证据 + 一句现象。
3. 数"符合"项算分（§打分）。
4. 把违反项归类喂 [`violation-catalog.md`](violation-catalog.md)，把可落地的改向喂环节④/⑤。

> 单点不确定时下钻：runtime 归位用 [`runtime-explicitness.md`](runtime-explicitness.md)；事实定级用 [`fact-grade-classification.md`](fact-grade-classification.md)；封装形态用 [`fa/protocols/component-protocol.md`](../protocols/component-protocol.md)；组织形态用 [`fa/protocols/capsule-protocol.md`](../protocols/capsule-protocol.md)。

---

## 七点清单

### ① Runtime 显式化
→ 判据本质见 [`tao/dimension-effect.md`](../../tao/dimension-effect.md)（显式 runtime）与 [`tao/depa-paradigm.md`](../../tao/depa-paradigm.md)（核心公式）。

- [ ] 这段逻辑能否写成 `output = fn(runtime, input, config)`，长生命周期依赖**显式注入 runtime**，而非藏在全局 / 闭包 / 对象实例 / 环境变量里？
- 符合信号：core 函数所有依赖来自 runtime 参数；同 runtime 同 input 可复现。
- 违反信号：core 里出现 `global` / 模块级单例 / `os.environ` / 现 new 客户端 / 现 `get_app`。

### ② Data 维
→ [`tao/dimension-data.md`](../../tao/dimension-data.md)。

- [ ] 数据是不是一等公民——状态能否从**事件/日志重建**，是否有清晰的"谁是这块数据的唯一写入者"，衍生有没有反写上游？
- 符合信号：有 append-only 事件源 + reducer 投影；事实源单写、衍生只读。
- 违反信号：多个半事实源同时生效；衍生投影反写事实；状态只活在内存对象里、无可重建源。下钻定级用 [`fact-grade-classification.md`](fact-grade-classification.md)。

### ③ Effect 维
→ [`tao/dimension-effect.md`](../../tao/dimension-effect.md)。

- [ ] 副作用是否**从核心逻辑分离**——effect 以契约/factory 声明在 runtime，编排在 factory/bootstrap，core 逻辑保持纯/可测，contract 与 logic 分层？
- 符合信号：core 不直接 IO；副作用契约与组装分离；contract 文件不写编排。
- 违反信号：core 里直接读写库/网络/文件；副作用契约与业务编排糊在一起；config 里塞函数对象。

### ④ Processor 维
→ [`tao/dimension-processor.md`](../../tao/dimension-processor.md)。

- [ ] 处理/分发是否**标准化**——多策略经枚举 id 注册表分发而非 `if/elif` 字符串分支；command（要应答）与 message（单向通知）边界是否清楚？
- 符合信号：`register_*`/`get_*` 注册表 + 占位齐备（见 [`fa/protocols/capsule-protocol.md`](../protocols/capsule-protocol.md) §4）；command/message 语义分明。
- 违反信号：新增策略要改 core 的 if/elif；command 与 message 混用（该等应答的发完不管、该单向的却阻塞等）。

### ⑤ Actor 维
→ [`tao/dimension-actor.md`](../../tao/dimension-actor.md)。

- [ ] 异步协作是否**消息化**——经 mailbox/队列通信而非共享可变状态直接互改；调度（fiber/task）与身份（actor）是否分离；有无 selective receive？
- 符合信号：协作方只通过消息往来；调度载体与 actor 身份解耦。
- 违反信号：多任务直接改同一可变对象（无 mailbox）；身份与调度纠缠；用裸锁糊共享状态替代消息。
- 注：纯同步、无并发协作的逻辑此点可标 `不适用`，不计入分母。

### ⑥ 避免过度设计
→ [`tao/boundaries-and-vendor.md`](../../tao/boundaries-and-vendor.md)。

- [ ] 复杂度是否被**当前真实需求**驱动，而非"将来可能"——有没有同构却强造的空壳 adapter、没有第二实现的抽象层、为单一用例预留的多态？
- 符合信号：每层抽象都有≥2 个真实实现或明确近期需求支撑。
- 违反信号：outer/inner 同构却套空 adapter；只有一个实现的策略注册表；"以防万一"的配置开关无人用。

### ⑦ Vendor 原语优先
→ [`tao/boundaries-and-vendor.md`](../../tao/boundaries-and-vendor.md)。

- [ ] 该用平台/框架/标准库**已有原语**的地方，是否优先用了它，而非自造轮子（自写调度器/序列化/重试/状态机替代成熟原语）？
- 符合信号：能映射到 vendor 原语的需求都映射了；自造仅限确无原语处。
- 违反信号：手搓了框架已提供的能力；为"完全掌控"重造标准库已有的机制。

---

## 打分

只数"符合"项；标 `不适用` 的点从分母剔除（常见于 ⑤ Actor 在纯同步逻辑）。

| 符合点数 | 符合度 | 解读 | 下一步 |
|----------|--------|------|--------|
| 6–7 | 高 | 形态正、边界清，DEPA 对齐良好 | 仅记零散改进进 backlog，不立项重构 |
| 4–5 | 混合 | 部分维度成立、部分明显违反 | 挑违反维度做定向切片，按环节⑤排序 |
| < 4 | 低，需重构 | 多维不成立，结构性问题 | 进环节④收敛 + 环节⑤切片，第一批先攻事实源/runtime 显式化 |

> 分数是**指针不是判决**：低分指向"哪些维度最值得改"，具体改什么、先改什么由环节④⑤定，且**建议不等于改造**（见 [`SKILL.md`](../../SKILL.md) 关键不变量）。

---

## 与脉络、目录的衔接

- 环节③ 盘点时对每个评审对象跑一遍本清单，结果落 `inventory/`。
- 环节④ 用低分维度定收敛重点；违反项归 [`violation-catalog.md`](violation-catalog.md) 对应分组。
- 七点里 ①③ 与测量尺 1、④⑥⑦ 与测量尺 2 / boundaries 互为印证；冲突时以**观测证据**为准，不以本清单口径压观测。
