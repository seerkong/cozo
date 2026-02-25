> 来源注记：复制自 ~/.claude/skills/depa-expert@2026-07-06（fa/rubrics/runtime-explicitness.md）。本项目真源（src/Om.Depa/rubrics/）优先演化，与全局 skill 双源各自演化——见 track expand-depa-detection-rules decisions.md。

# 核查表 — runtime / input / config 归位判据

> 给定**一个字段或一个依赖**，判它该进 `runtime`（哪一层）、还是 `input`、还是 `config`。这是测量尺 1 [`fa/protocols/component-protocol.md`](../protocols/component-protocol.md) 的逐字段下钻工具——尺给框架，本核查表给"这一个字段往哪放"的决策表与红灯。
>
> 核心公式（见 [`tao/depa-paradigm.md`](../../tao/depa-paradigm.md)）：
>
> ```text
> output = fn(runtime, input, config)
> ```
>
> runtime 内部按**角色维度**分类（静态配置 / 副作用契约 / 应用作用域资源 / 不可变外部上下文 / 可变内部上下文……），定义见 component-protocol §2；这些是判断维度而非强制字段名。runtime 自身可薄可厚（扁平 / 分组 / 跨域嵌套 / 响应式衍生 / 领域分面），形态谱系与跨形态不变量见 [`tao/runtime-paradigm.md`](../../tao/runtime-paradigm.md)——本核查表判"某字段归哪个角色"，不要求 runtime 长成某个固定形态。

---

## 决策表（拿一个字段逐问，命中即定位）

按顺序问，第一个命中的行给出归位。

归位列给的是**角色**（runtime 的某个角色 / input / config），不是某个项目的具体字段名。项目可以把这些角色平铺，也可以聚合成对象、或叠加更厚的结构。

| # | 问句 | 命中则归位 | 例 |
|---|------|------------|-----|
| 1 | 它是"怎么产生副作用"的契约 / factory（而非某次副作用的结果）？ | runtime · **副作用契约** | 模型工厂、工具构造器 |
| 2 | 它是 app/会话作用域资源（db / openapi / http / mcp / 已加载 prompt / registry）？ | runtime · **应用作用域资源**（registry） | 应用级 registry、db 句柄集 |
| 3 | 它在**整个调用过程中会被改写**、需跨步骤存活？ | runtime · **可变内部上下文** | 已绑定的工具、已初始化标记、跨步骤累积状态 |
| 4 | 它是由外层组装的、请求级**不可变**输入，调用内只读、跨多次内部调用复用？ | runtime · **不可变外部上下文** | 调用者身份、scope、空间 key、指令 |
| 5 | 它是进程级静态开关 / 默认值 / 上限，不随单次调用变？ | runtime · **静态配置（options）** | 最大步数、资源路径 |
| 6 | 它是**这一次调用专属**的业务数据载荷？ | `input` | 用户问题、本次代码字符串、本次事件 payload |
| 7 | 它是**这一次调用**的枚举标识 / 静态开关（选哪个 adapter、单次 feature flag）？ | `config` | adapter 枚举 id、"是否包含某段摘要"开关、兼容模式 |

> 兜底原则（三问，与 component-protocol §2 一致）：
> - 跨不跨单次调用？跨 → runtime；不跨 → input/config。
> - 调用内会不会被改写？会 → 可变内部上下文；不会 → 不可变外部上下文 / 静态配置 / 应用作用域资源。
> - 是 payload 还是配置？业务数据 → input；枚举/开关 → config。

判定结果落工作区 `inventory/`（归位表）与 `boundary/`，带 `path:line`。

---

## 边界辨析（最容易判错的几对）

- **外部上下文 vs input**：都来自"外面"。区别在生命周期——外部上下文在请求内被多次内部调用复用且不可变（如调用者身份）；input 是单次内层调用的 payload（如这一步要执行的代码）。同一个值若每次内层调用都换，是 input；若整个请求恒定，是外部上下文。
- **内部上下文 vs 外部上下文**：可变性。会被回写的（运行时变量、已绑定模型）进可变内部上下文；frozen 只读的进不可变外部上下文。**把可变状态塞进 frozen 外部上下文** 或 **把请求级只读值塞进可变内部上下文** 都是错位。
- **config vs 静态配置(options)**：作用域。config 是单次调用配置（这次用流式还是整包）；options 是进程级静态默认（最大步数）。单次会变的开关进 config，进程级恒定的进 options。
- **副作用契约 vs 内部上下文**：契约是"怎么产生副作用"的 factory（恒定）；某次副作用产出的可变状态（如已创建的模型实例）进可变内部上下文。契约和契约的产物不在同一类。

---

## 红灯（命中即 GAP，归 [`violation-catalog.md`](violation-catalog.md) 的"分层类"）

| 红灯 | 现象 | 为什么错 | 观测方式 |
|------|------|----------|----------|
| 长生命周期对象藏在 input/config | registry / client / session 对象通过 input 偷渡，或塞进 config | 稳定依赖应显式注入 runtime；藏在请求里使核心公式退化、不可复现 | 看 input/config 类型字段里有无对象图、client、registry |
| 函数对象塞 config | config 字段类型出现 `Callable` / 闭包 / 回调 | 函数是 effect 契约，属 runtime.effect，不是单次配置 | grep config dataclass 里的 `Callable` |
| runtime = any | runtime 类型标成 `Any` / `dict` / 无结构大袋子 | 失去角色归位，等于没显式化；字段来源不可审 | 看 runtime 类型注解是否结构化（dataclass / record / 强类型结构） |
| 业务逻辑写在 runtime dataclass 方法里 | `runtime.do_business()` 这类方法承载业务 | runtime 应是纯数据；逻辑应是以 runtime 为首参的外部函数（DOP 不成立的最强信号） | 看 runtime 类里有无非数据方法 / 业务分支 |
| config 重复 runtime 字段 | 同一依赖在 runtime 和 config 两处都有 | 两处真源必漂移；该依赖只该有一个归处 | 比对 runtime 与 config 字段名 |
| core 自己发现依赖 | core logic 里现取全局 app / 重新加载 registry / 重算工作目录 | 依赖应外层加载一次沿 runtime 下传；core 自取 = runtime 未显式化 | 看 core 函数体有无加载/发现调用 |

---

## 与脉络、尺的衔接

- 环节②"某字段属 runtime/input/config"直接跑本决策表；环节③把归位表落 `inventory/`。
- 本核查表是 component-protocol §2/§3/§5检查 1、2 的字段级展开；判完汇回该尺的"DOP 约束成立度"。
- 红灯喂 [`fa/rubrics/depa-conformance.md`](depa-conformance.md) 的 ①Runtime 显式化 与 ③Effect 维，以及 [`violation-catalog.md`](violation-catalog.md)。
