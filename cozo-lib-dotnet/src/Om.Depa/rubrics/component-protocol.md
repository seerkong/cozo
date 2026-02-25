> 来源注记：复制自 ~/.claude/skills/depa-expert@2026-07-06（fa/protocols/component-protocol.md）。本项目真源（src/Om.Depa/rubrics/）优先演化，与全局 skill 双源各自演化——见 track expand-depa-detection-rules decisions.md。

# 测量尺 1 — 标准组件协议（输入-输出-配置-运行时分层）

> 这是 depa-expert 在 **module 范围**量"一段逻辑封装得对不对"的第一把尺。它把一段代码量到核心公式上：
>
> ```text
> output = fn(runtime, input, config)
> ```
>
> 主要服务六步分析脉络的 ②（字段该属哪层）与 ③（runtime 归位表 + 封装差距盘点）。判据本身的"为什么"在 [`tao/dimension-effect.md`](../../tao/dimension-effect.md) 与 [`tao/depa-paradigm.md`](../../tao/depa-paradigm.md)；**runtime 自身的形态谱系（丰俭由人）与跨形态不变量在 [`tao/runtime-paradigm.md`](../../tao/runtime-paradigm.md)——本尺不预设固定层数，只引用它的不变量**；这里只给可勾的清单与可观测信号。
>
> 与本尺配套的逐字段归位判据见 [`fa/rubrics/runtime-explicitness.md`](../rubrics/runtime-explicitness.md)（决策表 + 红灯）。

---

## 0. 这把尺测什么、不测什么

- **测**：一段处理逻辑（一个 handler / agent / tool / service 方法）的"输入-输出-配置-运行时"四分是否成立；副作用是否从核心逻辑分离；runtime 字段是否归位（按 [`tao/runtime-paradigm.md`](../../tao/runtime-paradigm.md) 的不变量，不要求长成某个固定形态）；要不要 adapter。
- **不测**：模块的目录组织、capsule 化程度——那是测量尺 2 [`capsule-protocol.md`](capsule-protocol.md) 的活。一段逻辑可以"符合本尺"但所在模块"未 capsule 化"，反之亦然。

判据是**形态**，不是库。目标项目不用 depa-* / dataclass / Python 也照量——只问"长生命周期依赖有没有显式注入、单次 payload 有没有混入稳定依赖、副作用契约有没有从核心逻辑分离"。

---

## 1. 完整封装流程（一段逻辑从外到内再到外）

一个标准组件的调用，从 outer 边界进、到 core logic、再回 outer。按 DEPA 的 D/P 分离，这条流程是**一条显式数据血缘（D）**被**一组纯处理器（P）**串起来——数据是名词、处理器是 `data → data` 的纯函数，两块分开量。

**数据血缘（D · 只有数据）**

```text
outer{runtime · input · config}      调用域边界数据（进）：类型明确、不掺业务判断
   → derived                          轻量派生数据
   → inner{runtime · input · config}  内层三件套数据
   → inner output                     业务产出数据
   → outer output                     调用域边界数据（出）：类型稳定、与传输/协议解耦
```

**处理器（P · 只有逻辑）**：逐个问"它在不在、还是被压扁了"。读入/产出列是它碰的**数据**，达标/违反列是它这段**逻辑**对不对——同一行里数据列与逻辑列各管各的。

| 处理器 | 读入（D） | 产出（D） | 达标信号 | 违反信号（可观测） |
|--------|-----------|-----------|----------|---------------------|
| outer_derived_adapter | outer rt/in/cfg | derived | 纯计算；不需要时显式返回空派生 | 派生混入 IO / 重副作用；或本该派生的值散落在 core 里反复重算 |
| inner_runtime_adapter | outer rt/in/cfg + derived | inner runtime | runtime 构造在此、不在 core 里；可单独测试 | core 自己去 outer 对象里掏依赖；runtime 在 core 内部 new 出来 |
| inner_input_adapter | outer rt/in/cfg + derived | inner input | 只抽这次调用的单次 payload；可单独测试 | 通过 input 偷渡稳定依赖（registry / client / session） |
| inner_config_adapter | outer rt/in/cfg + derived | inner config | 只映射枚举 id + 开关；可单独测试 | config 里塞函数对象 / 共享可变状态 |
| core_logic（业务） | inner runtime / input / config | inner output | 不依赖隐式全局；所有依赖来自 runtime 参数；同输入同 runtime 可复现 | core 读全局单例 / 环境变量 / 模块级可变状态；core 里现 new 客户端 |
| output_adapter | outer rt/in/cfg + derived + inner output | outer output | inner output 转回 outer 契约；无需转换时显式 identity 透传 | core 直接返回 framework 专有对象，输出与传输耦合 |

> inner_runtime / inner_input / inner_config 三个处理器各自独立、可单独测试；糊成一坨就是违反。outer input / outer output 是这条血缘的首尾边界数据，不是处理器。

> "满分样板"长什么样（通用形态，非要求）：一个入口函数把这组处理器显式串成一条可命名的链，例如 `input_adapter → core_logic → output_adapter`，或更细的 `build_runtime → build_input → build_config → run_core → adapt_output`。每个处理器是一个独立可测的纯函数，core 只拿 `(runtime, input, config)`、不自己去 outer 对象里掏依赖——这条显式链就是达标信号。

**压扁信号**：现有代码里 inner_runtime / inner_input / inner_config 三个处理器整段缺失，outer 直接灌进一个大对象方法里现取现算——这是 module 范围最常见的"未封装"。

---

## 2. runtime 归位（按不变量判，不按固定层数判）

> ⚠️ runtime **没有唯一正确的形状**。它可以是几把扁平字段，也可以按角色分组、跨域嵌套、带响应式 private/public 衍生、或拆成多领域分面——形态谱系与"何时用哪种"在 [`tao/runtime-paradigm.md`](../../tao/runtime-paradigm.md)。本尺**不要求**目标 runtime 长成某几层；它量的是不变量是否成立、字段有没有归错位。

量一段逻辑的 runtime 时，逐字段按下面四个**角色维度**问"它属哪类"。这些是判断维度，**不是强制字段名**——项目可以把它们平铺、也可以聚合成对象；判的是"有没有按生命周期/可变性分清楚"，而非"有没有恰好这几个层"。

| 角色维度 | 装什么 | 生命周期 / 可变性 | 达标信号 | 错位（红灯） |
|----------|--------|-------------------|----------|----------|
| 静态配置（进程级 options） | 静态开关、默认值、上限 | 进程级，不可变 | 全是标量/枚举/路径，无对象图 | 把单次调用的 feature flag 塞进来（应进 config） |
| 副作用契约 / factory | "怎么产生副作用"的契约或工厂 | 进程级，契约不可变 | 只声明契约，不在此处执行业务编排 | 在契约字段里写业务编排；或契约缺失、core 直接 IO |
| 外部上下文（请求级不可变） | 由外层组装、请求级**只读**的值 | 请求级，frozen | 请求内只读；不可变值对象 | 在 core 里被回写；或单次 payload 混进来（应进 input） |
| 内部上下文（跨步骤可变） | 跨调用/跨步骤存活的**可变**状态 | 跨步骤，可变 | 显式承载、由 runtime 持有 | 可变状态藏在闭包/对象实例里；或本该可变的被塞进 frozen 外部上下文 |

> 复杂 runtime 还会在上面四类之外**叠加**：跨域子 runtime、immutable/mutable 快照、响应式 private/public 数据面、领域分面——这些的判据见 [`tao/runtime-paradigm.md`](../../tao/runtime-paradigm.md) §3 谱系与 §4 不变量。本尺只要求把"配置 / 副作用契约 / 不可变上下文 / 可变状态"这四类的**归位**判清；更厚的形态用 runtime-paradigm 的不变量去量。

**归位自检三问**（形态无关）：
- 这个依赖的生命周期跨不跨单次调用？跨 → runtime，不跨 → input/config。
- 它在调用内会不会被改写？会 → 内部上下文（可变），不会 → 外部上下文 / 静态配置（不可变）。
- 它是"怎么产生副作用"的契约还是"具体一次副作用的结果"？契约 → 副作用契约字段，结果 → 不该在 runtime。

---

## 3. input 与 config 只放什么

runtime 是长生命周期；input/config 是单次调用。两者最常见的污染是"把稳定依赖偷渡进 input/config"，使核心公式退化。

### input — 单次调用的 payload，仅此

达标：只放这一次调用专属的数据（用户问题、一次执行的 `code` 字符串、本次事件载荷）。
红灯：
- 通过 input 偷渡稳定依赖（registry、client、session 对象）——长生命周期对象藏在请求里。
- input 里出现函数对象 / 回调（那是 effect 契约，应进 runtime）。
- input 字段在多次调用间被复用、被缓存——说明它本质是 runtime。

### config — 枚举标识 + 静态策略，仅此

达标：只放枚举式的"用哪个 adapter"标识、单次调用的开关（如"是否包含某段摘要"、"兼容模式"这类布尔）、静态策略数值。
红灯：
- **config 里塞函数对象**（factory / 闭包 / 回调）——这是 runtime/effect，不是 config。
- config 重复 runtime 已有字段——同一依赖两处真源，必然漂移。
- config 承载共享可变状态——config 应是单次调用的不可变配置。

> "干净样板"长什么样：一份 config 整体只有"若干 adapter 枚举 id + 几个布尔/数值开关"，零函数对象、零稳定依赖——所有 adapter 选择都是 `*AdapterId` 这类枚举，运行时再按枚举查表拿到真正的实现。config 里出现任何对象图、闭包或长生命周期依赖，就是污染。

---

## 4. 何时需要 adapter、何时不需要

adapter 是 inner_runtime / inner_input / inner_config（把 outer 拆成 inner 三件套的那组处理器）的承载者。它**唯一存在理由**是"outer runtime 与 inner component runtime 结构不一致"。

| 情形 | 要不要 adapter | 判据 |
|------|----------------|------|
| outer runtime 与 inner runtime 结构不同（字段名/形态/裁剪不同） | **要** | inner component 故意比 outer 小、只取需要的字段 → 需要映射层把 outer→inner |
| 需要注入应用作用域闭包（把某个应用级 API 闭包合并进可复用逻辑的依赖集） | **要** | raw 可复用逻辑保持纯函数，闭包捕获在 adapter 层 |
| outer 与 inner 同构、字段一一对应、无注入 | **不要** | 直接调 core logic 即可；强造 adapter 是过度设计（见 [`tao/boundaries-and-vendor.md`](../../tao/boundaries-and-vendor.md) 避免过度设计） |
| 派生为空、输出无需转换 | 用 identity / null 默认 | 有"空派生"/"恒等输出"这类默认 helper 时直接用，不写样板胶水 |

**adapter 链路完整性检查**（要 adapter 时，每个处理器都该有承载者）：

```text
outer_derived_adapter → inner_runtime_adapter → inner_input_adapter
   → inner_config_adapter → core_logic_adapter → output_adapter
```

- 缺 `inner_runtime_adapter`：core 自己 new runtime —— inner runtime 那段 transform 漏了。
- 缺 `output_adapter` 而又确实需要转换：core 直接吐 framework 对象 —— output transform 漏了。
- adapter 里写业务逻辑：adapter 越界，应只做映射；业务回 core logic。

> 反模式（可直接当红灯用）：
> - 明明需要 adapter，却在 outer tool wrapper 里直接调 component logic（跳过 ③）。
> - 只通过全局变量/单例传递某个应用级 registry（绕过 runtime 注入）。
> - 让可复用组件无意义地 import outer-domain 模块（inner 反向依赖 outer）。
> - 在 runtime 数据容器里写业务逻辑方法（见下一节 DOP 约束）。

---

## 5. 现状分析四项检查（量一段逻辑时逐项裁决）

对一段待评估逻辑，跑这四项，每项给 `PASS | GAP | BLOCKED`（裁决词汇见 [`protocols.md`](../../protocols.md)），结论带 `path:line` 证据，落到工作区 `inventory/` 与 `convergence/`。

### 检查 1：runtime 归位

- [ ] 长生命周期依赖（client / registry / factory / 已加载模型与 prompt）是否都在 runtime，而非全局/闭包/对象实例？
- [ ] runtime 字段是否各归其位（§2 四个角色维度 + 归位自检三问全过；更厚形态再按 [`tao/runtime-paradigm.md`](../../tao/runtime-paradigm.md) §4 不变量过一遍）？
- 观测：搜 core 函数体内的 `global` / 模块级单例引用 / 环境变量直读 / 现 `new`/现取全局 registry —— 命中即 GAP。

### 检查 2：职责分层（input/config 纯净 + 副作用分离）

- [ ] input 只装单次 payload、config 只装枚举+静态策略（§3 红灯全不命中）？
- [ ] 副作用契约（effect）与副作用编排是否分离——契约在 runtime 声明、编排在 factory/bootstrap，**不写在 contract 文件里**？
- 观测：config 字段类型里出现 `Callable` / 函数 → GAP；contract 模块 import 了重型副作用实现且非纯类型用途 → GAP。

### 检查 3：适配链路完整

- [ ] 若 outer/inner 不一致：六个处理器的 adapter 是否齐全、各司其职（§4 链路）？
- [ ] 若一致：是否**没有**强造多余 adapter（避免过度设计）？
- 观测：tool wrapper 直接调 core logic 却存在结构差异 → GAP（漏 inner transform 那组处理器）；同构却套了空壳 adapter → GAP（过度设计）。

### 检查 4：DOP 约束 —— `fn(runtime, input, config)` 能否成立

这是本尺的**总判据**，前三项是它的分解。

- [ ] core 逻辑能否被改写/已是 `fn(runtime, input, config)` 形态，不依赖隐式全局？
- [ ] runtime 类是否为**纯数据**（dataclass / 等价结构），**没有业务逻辑方法**？
- 观测红灯：**业务逻辑写在 runtime dataclass 的方法里**（`runtime.do_business()`）——这是 DOP 不成立的最强信号，runtime 应是纯数据、逻辑应是以 runtime 为首参的外部函数。

四项裁决汇总 → 给该逻辑一个"标准组件符合度"小结，连同证据落 `convergence/`，喂给环节④的处置决策与 vendor 映射。

---

## 与脉络、其他尺、核查表的衔接

- 环节② 用本尺 §2/§3 判"字段该属 runtime 哪层 / input / config"。
- 环节③ 用本尺 §5 产出"输入-输出-配置-运行时归位表 + 封装差距"，每行带 `path:line`。
- 逐字段归位拿不准 → 转 [`fa/rubrics/runtime-explicitness.md`](../rubrics/runtime-explicitness.md) 的决策表。
- DEPA 维度打分时，本尺的"DOP 约束成立度"直接喂 [`fa/rubrics/depa-conformance.md`](../rubrics/depa-conformance.md) 的 ①Runtime 显式化 与 ③Effect 维。
- 模块组织/目录层面接着量 → 转测量尺 2 [`capsule-protocol.md`](capsule-protocol.md)。
