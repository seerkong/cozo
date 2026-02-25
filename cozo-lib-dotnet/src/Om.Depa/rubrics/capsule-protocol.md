> 来源注记：复制自 ~/.claude/skills/depa-expert@2026-07-06（fa/protocols/capsule-protocol.md）。本项目真源（src/Om.Depa/rubrics/）优先演化，与全局 skill 双源各自演化——见 track expand-depa-detection-rules decisions.md。

# 测量尺 2 — capsule 协议（同模块级组织规范 + 判定）

> 这是 depa-expert 在 **module 范围**量"一个模块组织得对不对"的第二把尺。测量尺 1 [`component-protocol.md`](component-protocol.md) 量一段逻辑的 `fn(runtime,input,config)` 形态；本尺往外一层，量一个模块作为 **capsule** 的目录布局、入口纯度、依赖方向。
>
> 主要服务六步分析脉络的 ③（capsule 化差距盘点）与 ④（capsule 拆分/补齐的处置）。"为什么这样组织是对的"在 [`tao/dimension-processor.md`](../../tao/dimension-processor.md) 与 [`tao/boundaries-and-vendor.md`](../../tao/boundaries-and-vendor.md)；这里只给可勾清单与可观测信号。

---

## 0. 什么是 capsule

一个 capsule = 一个**自包含、单一公开入口、内部隐藏、对外只暴露稳定契约**的模块单元。它把"标准组件协议的封装流程"固化成目录约束：core_logic 是唯一入口，adapter 按枚举 id 布线，internals 不可被外部 import，types 是对外契约。

判据是**组织形态**，不是某个库或语言。任何语言只要满足"单一入口 + 枚举布线 + 内部隔离 + 单向依赖"，就算 capsule 化。

---

## 1. 目录布局

一个达标 capsule 的目录骨架（通用形态，`<capsule>` 为 capsule 名占位）：

```text
<capsule>/
  __init__.py            ← 只 re-export 公开契约 + 入口；不暴露 internals
  core_logic.py          ← 唯一稳定入口（run_<capsule>），编排封装流程
  adapter_registry.py    ← 按枚举 id 注册/取 adapter；含占位实现
  <axis>_adapters/       ← 各适配轴一个分目录（input_adapters/ output_adapters/
                            runtime_adapters/ resource_adapters/ persistence_adapters/
                            controller_adapters/ orchestrator_adapters/ ...）
  internals/             ← 不可被外部 import 的实现细节
  types/                 ← 对外契约类型（contract/input/output/config/runtime/...）
                            —— 仅当包级 contract/logic 未分离时放在这里
```

**types 放哪取决于项目**：
- 若项目**包级已做 contract/logic 分离**（存在独立的 `*_contract` / `*_logic` 包）——对外类型应放在 **contract 包**，capsule 内不再自建 `types/`。
- 否则——capsule 自带 `types/`（或 `types/contract.py`）承载对外契约。

> 信号：同一项目里有的 capsule 把类型放 contract 包、有的塞自己 `types/`，且两者重复定义同一契约——这是"contract 边界没立稳"，记为 GAP（归 [`tao/boundaries-and-vendor.md`](../../tao/boundaries-and-vendor.md) 的 contract/logic 分离）。

---

## 2. 命名约定

| 元素 | 约定 | 形态例（占位） |
|------|------|----------------|
| 入口函数 | `run_<capsule>` / `run_<capsule>_async` | `run_<capsule>(input, config)` |
| adapter 轴目录 | `<axis>_adapters/` | `input_adapters/`、`runtime_adapters/`、`persistence_adapters/` |
| adapter 枚举 id | `<Capsule><Axis>AdapterId`（str Enum） | `<Capsule>RuntimeAdapterId.<VARIANT>`、`<Capsule>InputAdapterId.<VARIANT>` |
| 注册/取函数 | `register_<axis>_adapter` / `get_<axis>_adapter` | `register_input_adapter` / `get_input_adapter` |
| 配置类型 | `<Capsule>Config`，字段是各轴的 `*AdapterId` 默认值 | `<Capsule>Config { input_adapter: <Capsule>InputAdapterId, ... }` |

命名不统一本身不是违反，但**命名能直接读出布线**是达标信号：看 config 的字段类型就知道有几条适配轴、各轴有哪些 id。

---

## 3. core_logic 唯一入口签名

core_logic 是 capsule 的**唯一对外执行入口**，签名是 capsule 的公开契约面。

- 形态固定为标准组件形态：`async def run_<capsule>(input: <Capsule>Input, config: <Capsule>Config) -> <Capsule>Output`（入口只吃 input + config，runtime 在入口体内构造）。
- 入口体内**只做编排**：取 adapter → 调封装流程（见测量尺 1 的六处理器链）→ 返回。不在入口里写具体业务，不直接 IO。
- **签名变更 = 破坏性变更**。入参/出参类型一旦稳定，改它等于改 capsule 对外契约，所有调用方受影响。盘点时把入口签名当"接口快照"记下，作为后续 diff 的基线。

观测达标信号：`__init__.py` 只 re-export `run_<capsule>` 与 types，外部调用方只 import 这两者；从未见外部 import `<capsule>.internals.*`。

---

## 4. adapter 按枚举 id 注册 + 占位实现

每条适配轴是一张 `枚举 id → adapter 函数` 的注册表，集中在 `adapter_registry.py`。

达标模式（`<capsule>/adapter_registry.py` 通用形态）：
1. 每轴一个私有 dict：`_INPUT_ADAPTERS: dict[<Axis>AdapterId, Adapter] = {}`。
2. 每轴一对 `register_<axis>_adapter(id, adapter)` / `get_<axis>_adapter(id)`。
3. `get_*` 对未知 id 显式报错（`Unknown ... adapter: {id}`），不静默返回 None。
4. **占位齐备**：对枚举里每个 id `setdefault` 一个 `_unimplemented_*` 占位（`raise NotImplementedError`）——保证"枚举声明了的 id 必有条目，未实现也显式爆而非 KeyError"。
5. 真实 adapter 在各 `<axis>_adapters/` 文件里定义，由 `internals/adapter_bootstrap.py` 这类引导模块在 import 时注册。

观测违反信号：
- adapter 用 `if/elif` 字符串分发而非枚举注册表 —— 新增轴/分支要改 core，违反开放扩展（归 Processor 维）。
- `get_*` 命中未知 id 时静默回退/返回 None —— 隐藏未布线。
- 枚举里声明了 id 但注册表里没有任何条目（连占位都没有）—— 调用即 KeyError，布线不完整。

---

## 5. capsule 间单向依赖

capsule 之间只能**单向、经公开面**依赖。

允许：
- A 的 `core_logic` 依赖 B 的 `core_logic`（B 的 `run_<capsule>` 公开入口）。
- A 依赖 B 的 **public types**（B 的 contract / `types` 对外类型）。

禁止：
- A 依赖 B 的 `internals.*`（绕过公开面，触内部细节）。
- types **跨 capsule 借用 internal 类型**——A 的契约不应引用 B 没对外暴露的类型。
- 双向依赖 / 形成环（A→B 且 B→A）。

> 达标样板形态：上层 capsule A 的 `core_logic` 只 `from <B> import run_<B>` + `from <B>.types import <B>Input, <B>Output`——只碰 B 的入口与 public types；B 的 `core_logic` 同样只 import 下游 capsule C 的入口与 types。多个 capsule 的依赖呈单向链：`A → B → C`，无一处 import 对方 `internals`。

观测违反信号（grep 即可）：
- 跨 capsule 出现 `from <other_capsule>.internals` —— internals 隔离被打穿。
- B 的对外 `types` 里 import 了 B 自己的 `internals` 私有类型并暴露出去 —— 私有类型泄漏成公开契约。
- 依赖图里出现回边 —— 单向性破坏。

---

## 6. "已 capsule 化"判定表

对一个待评估模块，逐维裁决（`PASS | GAP | BLOCKED`，词汇见 [`protocols.md`](../../protocols.md)），结论带 `path:line`，落工作区 `inventory/` 与 `convergence/`。

| 维度 | 达标信号 | 未达标现象（可观测） |
|------|----------|----------------------|
| 目录布局 | core_logic / adapter_registry / `<axis>_adapters/` / internals / types(或 contract 包) 齐备且各就各位 | 平铺一堆 .py 无分层；adapter、internal、契约混在同一层 |
| 入口唯一 | 只有一个 `run_<capsule>` 对外；`__init__` 只 re-export 入口 + types | 多个对外入口；外部从模块各处 import 不同函数；无单一入口 |
| 类型纯净 | 对外类型在 contract 包或 `types/`；不泄漏 internal 类型；契约稳定 | 契约引用 internal 类型；同契约两处重复定义；类型与实现纠缠 |
| adapter 注册 | 枚举 id → adapter 注册表 + 占位齐备 + `get_*` 显式报错 | 字符串 if/elif 分发；无占位；未知 id 静默 |
| internals 隔离 | 无任何外部 import 触及 `internals.*` | 存在 `from <capsule>.internals import ...` 的外部引用 |
| 依赖指向 | 只依赖他 capsule 的 core_logic + public types，单向无环 | 触他 capsule internals；types 跨 capsule 借 internal；存在回边 |

六维全 PASS = "已 capsule 化"。任一 GAP = "部分 capsule 化"，进下表补救。

---

## 7. "哪里没达标"补救表

| 未达标维度 | 典型现象 | 补救方向（喂环节④处置 / 环节⑤切片） |
|------------|----------|----------------------------------|
| 目录布局 | 平铺无分层 | 先抽出 `internals/`（把实现细节下沉），再立 core_logic 单入口，最后归拢 adapter 轴目录 |
| 入口唯一 | 多对外入口 | 收敛为一个 `run_<capsule>`，旧入口降级为薄兼容壳委托给它（兼容期保留回归测试） |
| 类型纯净 | 契约引用 internal / 两处重复 | 把对外类型提到 contract 包或 `types/`；internal 类型不再出现在公开签名里 |
| adapter 注册 | 字符串 if/elif 分发 | 改造为枚举 id 注册表 + 占位 `_unimplemented_*`；新增轴只加注册不改 core |
| internals 隔离 | 外部 import internals | 把被外部依赖的部分提升为 public types 或经入口暴露；切断对 internals 的直接引用 |
| 依赖指向 | 触他 capsule internals / 有环 | 改走对方 core_logic + public types；环依赖**先**按事实源边界定 owner、拆出共享契约到 contract 包（轻解）；**切不掉的本质环** → 把环上一节点 actor 化，改发 command/message（见 [`../../tao/actor-paradigm.md`](../../tao/actor-paradigm.md)） |

补救建议遵守分析纪律：**建议不等于改造**（见 [`SKILL.md`](../../SKILL.md) 关键不变量）；每条补救写成可独立落地、可验收的切片，依赖顺序由环节⑤定。

---

## 与脉络、其他尺、核查表的衔接

- 环节③ 用 §6 判定表产出"capsule 化差距盘点"，每行带 `path:line`。
- 环节④ 用 §7 补救表把差距转成处置；§5 标出的依赖违规喂 [`fa/rubrics/violation-catalog.md`](../rubrics/violation-catalog.md) 的"分层类"。
- 入口签名是测量尺 1 [`component-protocol.md`](component-protocol.md) 的 `fn(runtime,input,config)` 的对外面——两尺在 core_logic 处接合：本尺管"它是不是唯一入口、依赖方向对不对"，尺 1 管"入口体内封装流程对不对"。
- §4 的"枚举注册而非 if/elif"直接喂 [`fa/rubrics/depa-conformance.md`](../rubrics/depa-conformance.md) 的 ④Processor 维。
