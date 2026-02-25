> 来源注记：复制自 ~/.claude/skills/depa-expert@2026-07-06（fa/rubrics/violation-catalog.md）。本项目真源（src/Om.Depa/rubrics/）优先演化，与全局 skill 双源各自演化——见 track expand-depa-detection-rules decisions.md。

# 核查表 — 违反信号的红灯目录

> 跨 DEPA 四维 + 事实源边界 + 分层 + 过度设计的"红灯"快速对照表。扫描一个项目时拿它逐条比对，命中即记一条带证据的现象。这是 [`shu/promote.md`](../../shu/promote.md) 晋升新模式的**归处**——跨多次分析复发的违反模式晋升进本目录（晋升触发条件见 [`protocols.md`](../../protocols.md) §6）。
>
> 每条格式固定：**问题现象 → 所属维度/原则 → 典型证据形态 → 建议改造方向**。
> - "问题现象"：扫描时能直接看见的可观测信号。
> - "所属维度/原则"：链回判据本源（`tao/dimension-*.md` 或 `tao/*.md`）。
> - "典型证据形态"：该红灯在代码里长什么样、grep 什么、记成什么 `path:line`。
> - "建议改造方向"：喂环节④收敛/环节⑤切片的方向；**建议不等于改造**（见 [`SKILL.md`](../../SKILL.md) 关键不变量）。

裁决用一般审查词汇 `PASS | GAP | BLOCKED`（见 [`protocols.md`](../../protocols.md)）。任何命中只是"带证据的现象"，是否处置交环节④⑤。

---

## A. Data 类（数据维 — [`tao/dimension-data.md`](../../tao/dimension-data.md)）

| 问题现象 | 所属 | 典型证据形态 | 建议改造方向 |
|----------|------|--------------|--------------|
| 状态只活在内存对象里、无可重建源 | Data | 找不到事件源/日志，状态丢了无法恢复 | 引入 append-only 事件源 + reducer 投影，使状态可重建 |
| 多个半事实源对同一真相同时生效 | Data / 事实源 | 同一节点两条都"被当真"的写入路径（内存权威 + 磁盘快照都被当源） | 定唯一 owner（[`fact-grade-classification.md`](fact-grade-classification.md)），其余降级为投影/快照 |
| 状态靠"先写文件再回读"传递 | Data | core 写文件后立刻回读同一文件取状态 | 内存权威单写，落盘作旁路；读路径改读内存 1 级 |
| 投影/读模型与事实纠缠、改不动 | Data | 同一对象既被当源写又被当缓存读 | 拆出 6 级投影（可重建、单向、不反写上游） |

## B. Effect 类（副作用维 — [`tao/dimension-effect.md`](../../tao/dimension-effect.md)）

| 问题现象 | 所属 | 典型证据形态 | 建议改造方向 |
|----------|------|--------------|--------------|
| 核心逻辑里直接 IO | Effect | core 函数体内直接读写库/网络/文件、现 new client | 副作用契约提到 runtime.effect，core 只调契约、保持纯/可测 |
| 副作用契约与编排糊在一起 | Effect | contract 文件里写了副作用组装/编排逻辑 | 契约（怎么产生）留 contract，编排（怎么组装）移 factory/bootstrap |
| 隐式全局依赖 | Effect / 分层 | core 读 `global` / 模块级单例 / `os.environ` | 长生命周期依赖显式注入 runtime（[`runtime-explicitness.md`](runtime-explicitness.md)） |
| 应用级 registry 只通过全局变量传递 | Effect / 分层 | 全局单例持 registry，core 自取 | 入口层加载一次，沿 runtime 的应用作用域资源字段下传 |

## C. Processor 类（处理/分发维 — [`tao/dimension-processor.md`](../../tao/dimension-processor.md)）

| 问题现象 | 所属 | 典型证据形态 | 建议改造方向 |
|----------|------|--------------|--------------|
| 多策略用 if/elif 字符串分发 | Processor | 新增策略要改 core 的一长串 `if name == "..."` | 改枚举 id → adapter 注册表（[`fa/protocols/capsule-protocol.md`](../protocols/capsule-protocol.md) §4） |
| 未知 id 静默回退/返回 None | Processor | `get_*` 命中未知分支不报错 | `get_*` 对未知 id 显式 raise；枚举每 id 配占位 `_unimplemented_*` |
| command 与 message 边界混用 | Processor | 该等应答的发完不管，或该单向通知的却阻塞等结果 | 分清 command（要应答）/ message（单向），语义对齐调用方式 |
| 处理逻辑对调用域有感知 | Processor | 可复用组件 import outer-domain 模块来分支 | 组件逻辑对调用域无感知，差异经 adapter/config 注入 |

## D. Actor 类（异步通信维 — [`tao/dimension-actor.md`](../../tao/dimension-actor.md)）

| 问题现象 | 所属 | 典型证据形态 | 建议改造方向 |
|----------|------|--------------|--------------|
| 并发任务直接改同一可变对象 | Actor | 多 task/线程无 mailbox 直接 mutate 共享对象 | 协作改经 mailbox/队列消息，去掉直接互改 |
| 用裸锁糊共享状态替代消息 | Actor | 满屏 lock 保护一块共享可变状态 | 把共享态收进单一 owner，外部经消息读写 |
| 调度与身份纠缠 | Actor | actor 身份与其 fiber/task 调度载体不可分 | 分离调度（fiber/task）与身份（actor） |
| 缺 selective receive、消息无序处理 | Actor | mailbox 无法按类型/优先级选择性接收 | 引入 selective receive，按需挑选消息 |
| 用隐式 DI 把循环依赖藏起来 | Actor / 分层 | 模块互相依赖成环，靠全局单例 / service locator / 延迟注入让编译器看不见环 | 先重切边界消环；切不掉的本质环把一节点 actor 化、改发 command/message，保持依赖显式单向（[`tao/actor-paradigm.md`](../../tao/actor-paradigm.md)） |

## E. 事实源边界类（[`tao/fact-source-truth.md`](../../tao/fact-source-truth.md)）

> 这一组对应 [`tao/fact-source-truth.md`](../../tao/fact-source-truth.md) §4 反写检查清单，是其"目录化"。命中同时记入 `boundary/backwrite-risks.md`。

| 问题现象 | 所属 | 典型证据形态 | 建议改造方向 |
|----------|------|--------------|--------------|
| checkpoint 读取后影响 live 控制流 | 事实源（规则④） | 正常路径读 5 级快照决定"下一步做不做" | 快照只在恢复/启动读；live 控制只读内存 1/3 级 |
| journal 字段被用来判断下一步 | 事实源（规则③） | live loop 读 4 级流水账某条作分支依据 | journal 纯旁路；live 判断改读权威/控制面 |
| UI 状态反向驱动主循环 | 事实源（规则②⑤） | 7 级控件/选中态不经 command 直接改 1/3 级事实 | 隔显式 message/command 边界，UI 只发命令 |
| 文件 mtime/存在性被用来判运行状态 | 事实源（规则③④） | 用快照/journal 文件的修改时间/存在性推断"是否在跑/暂停" | 运行状态读 3 级控制面，不读持久层元信息 |
| 投影算完反写源事实 | 事实源（规则②） | 6 级投影在重建/读取时顺手改了 1/2 级上游 | 投影单向只读上游、只供下游，去掉回写 |
| 快照夹带单次 payload | 事实源 / 分层 | 5 级 checkpoint 把 input payload 一起序列化（[`fact-grade-classification.md`](fact-grade-classification.md) 案例 D） | 快照只存 1–3 级权威/控制态，拆出 payload 不入快照 |

## F. 分层类（runtime/input/config 归位 + contract/logic 分离 — [`tao/boundaries-and-vendor.md`](../../tao/boundaries-and-vendor.md)）

> runtime/input/config 归位的字段级细则在 [`runtime-explicitness.md`](runtime-explicitness.md)；capsule 组织相关的红灯在 [`fa/protocols/capsule-protocol.md`](../protocols/capsule-protocol.md)。这里汇总常被扫描命中的几条。

| 问题现象 | 所属 | 典型证据形态 | 建议改造方向 |
|----------|------|--------------|--------------|
| 长生命周期对象藏在 input/config | 分层 | registry/client/session 经 input 偷渡或塞 config | 显式注入 runtime 对应层（[`runtime-explicitness.md`](runtime-explicitness.md)） |
| 函数对象塞 config | 分层 | config dataclass 字段类型出现 `Callable`/闭包 | 函数是 effect 契约，移 runtime.effect |
| config 重复 runtime 字段 | 分层 | 同一依赖在 runtime 与 config 两处都有 | 单一归处，删重复，消除漂移 |
| 业务逻辑写在 runtime dataclass 方法里 | 分层 / Effect | `runtime.do_business()` 承载业务 | runtime 设为纯数据，逻辑改外部函数以 runtime 为首参 |
| 外部 import 触及 `internals.*` | 分层 / Processor | `from <capsule>.internals import ...` 的外部引用 | 被依赖部分提升为 public types 或经入口暴露，切断对 internals 引用 |
| 跨 capsule 触对方 internals / types 借 internal | 分层 | `from <other_capsule>.internals`；契约引用他 capsule 私有类型 | 改走对方 core_logic + public types；共享类型提到 contract 包 |
| contract 与 logic 反向依赖 / 同契约两处重复定义 | 分层 | contract 包 import logic；或同一类型在 contract 包与 capsule `types/` 各定义一份 | 立稳 contract 边界，单处定义、单向依赖（contract←logic） |

## G. 过度设计类（[`tao/boundaries-and-vendor.md`](../../tao/boundaries-and-vendor.md)）

| 问题现象 | 所属 | 典型证据形态 | 建议改造方向 |
|----------|------|--------------|--------------|
| 同构却强造空壳 adapter | 过度设计 | outer/inner 字段一一对应却套了只透传的 adapter | 去掉空壳，直接调 core logic（[`fa/protocols/component-protocol.md`](../protocols/component-protocol.md) §4） |
| 只有一个实现的策略注册表/多态 | 过度设计 | 枚举只有一个 id、抽象基类只有一个子类 | 收敛为直接实现，待出现第二实现再抽象 |
| "以防万一"的配置开关无人用 | 过度设计 | config 字段全程默认值、无调用方传非默认 | 删未用开关；需要时再加 |
| 复杂度由"将来可能"而非真实需求驱动 | 过度设计 | 注释/PR 以"未来扩展""可能需要"论证抽象 | 按当前真实需求定复杂度，可扩展性留接缝不留空层 |
| 自造已有 vendor 原语 | 过度设计 / vendor 优先 | 手搓框架/标准库已提供的调度/序列化/重试/状态机 | 优先映射 vendor 原语，自造仅限确无原语处 |

---

## 怎么用这张表

1. **扫描期**：环节①③ 拿 A–G 逐组比对评审对象，命中即记一条现象（带 `path:line`）落 `inventory/incidents.md` 或对应 `boundary/*`。
2. **收敛期**：环节④ 把命中按"所属维度/原则"聚类，用"建议改造方向"列起草处置，再交环节⑤切片排序。
3. **晋升期**：跨多次分析复发的新红灯，经 [`shu/promote.md`](../../shu/promote.md) 增补进对应分组；被否决的方案、未稳定的猜测、单次过程噪音**不**晋升（见 [`protocols.md`](../../protocols.md) §6）。

> 本目录是"快速对照"，不替代判据本源。每条红灯的"为什么是违反"以其链接的 `tao/*` 为准；与 [`depa-conformance.md`](depa-conformance.md) 七点互为印证——清单打分发现的违反维度，在这里查具体红灯与改向。
