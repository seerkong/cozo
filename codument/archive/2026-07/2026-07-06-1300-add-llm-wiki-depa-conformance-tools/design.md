# DEPA 概念 → OM 本体映射设计（G1-T2）

> 目标：用 cozo-lib-dotnet 的 OM 层（DefineType / DefineAttribute / DefineRelation / DefineExistentialRule / Constraint+Validator）承载 depa-expert 的理论概念，使 DEPA 判定结果成为**可查询的图事实**，供 `depa_conformance` / `fact_grade_map` / `health_score` 工具消费。
>
> 理论源：`~/.claude/skills/depa-expert/`（tao/depa-paradigm、tao/runtime-paradigm、tao/fact-source-truth、fa/rubrics/violation-catalog、fa/rubrics/runtime-explicitness、fa/protocols/capsule-protocol）。
> 图事实源：`src/Om.CodeKnowledge/` 的 ck_* v2 schema（ck_symbol / ck_edge{CALLS,ACCESSES,IMPORTS,IMPLEMENTS,…} / ck_file / ck_entry_point）。

---

## 0. 总体架构决策

### 0.1 两层分工：ck_*（观测层）vs depa_*（本体层）

- **ck_\* 关系是原始观测**：符号、边、path:line。索引器产出，DEPA 不改它，只读。
- **depa_\* 是 OM 实体/关系承载的解释层**：把"这个 interface 是一个 contract""这个函数是 capsule 入口""这个字段是 grade-1 事实源"这类**判定结论**物化为 OM 实体与边。判定结论天然带不确定性与出处，所以每个 depa 实体都带 `assigned_by` / `confidence` / `evidence` 属性（见 §5）。
- **连接方式**：DEPA 实体不复制 ck_symbol 内容，通过属性 `symbol_id`（值 = ck_symbol.symbol_id）引用锚点符号。理由：ck_symbol 行不是 OM 实体（reindex 时整体重建），OM 边两端必须都是 OM 实体，故 depa 实体间用 OM Relation，depa→ck 用值引用属性 + Datalog join。

### 0.2 实体 id 约定

```text
depa:<type-short>:<stable-key>
例：depa:capsule:Om.Core
    depa:contract:ICozoOmStore@src/Om.Core/Contracts/ICozoOmStore.cs
    depa:factsource:CozoOmRuntime.SchemaCache
    depa:violation:E1@ck_edge(sym_a→System.IO.File.WriteAllText)
```

stable-key 优先用 ck_symbol.sym_key（跨 reindex 稳定），退化用 `name@path`。reindex 后由重建管线按 sym_key 重挂 `symbol_id`。

### 0.3 OM 表达力边界（影响 §3 的表达方式选择）

实测 OM 公开 API（`Om.Core/CozoOm.cs`、`Contracts/Models/OmModels.cs:113-153`）：

- `ExistentialRuleSpec = ForEach(type, where attr 条件) + Exists(rel, direction, toType)`，Mode=Check 时产出 `ExistentialViolation{Rule, EntityId, Message}`。**只能表达"正向存在性"**（每个 X 必须有一条 R 边到 Y），不能表达否定（"不得存在某边"）、不能表达计数（"至多一个"）。
- `DefineConstraintAsync + RegisterValidator`：C# 回调，任意逻辑，逐实体校验——可表达否定与计数，但是宿主代码不是声明式图事实。
- 结论（本设计的三档表达策略）：
  1. **正向必备** → ExistentialRule（Check 模式）。
  2. **负向红灯（存在即违反）** → Datalog 检测查询物化 `depa_violation` 实体 +违反边；ExistentialRule 只用来兜"每个 violation 必须挂 evidence"。
  3. **计数/唯一性**（多写入者、多入口）→ Datalog 聚合物化 violation；另注册 Validator 作为写入期防线（可选）。

---

## 1. DEPA 本体 Type 清单

全部经 `DefineTypeAsync` 定义，根类型 `depa_node`（公共属性挂根上，子类型继承）。

### 1.0 公共属性（挂 `depa_node`）

| 属性 | 类型 | 说明 |
|------|------|------|
| `symbol_id` | string | 锚点 ck_symbol.symbol_id（可空：capsule 锚 namespace/目录时锚 ck_file 或留空） |
| `sym_key` | string | 稳定键，reindex 后重挂用 |
| `path` | string | 证据主路径（冗余存，便于直接出 path:line） |
| `line` | int | 证据起始行 |
| `assigned_by` | string | `manual` \| `config` \| `heuristic` \| `llm` —— 判定出处 |
| `confidence` | float | 0-1，heuristic/llm 判定必填 |

### 1.1 Type 表

| Type | 承载的 DEPA 概念 | 专有属性 | 锚点（symbol_id 指向什么） |
|------|------------------|----------|---------------------------|
| `depa_capsule` | capsule-protocol §0 的自包含模块单元 | `name`、`root_path`（目录）、`entry_count`(computed)、`internals_path`（internals 目录 glob，默认 `**/Internals/**`） | 无单符号锚；`root_path` 锚 ck_file.path 前缀，成员经 §2 `capsule_contains` 或 path 前缀 join |
| `depa_contract` | 副作用契约 / 对外类型契约（Effect 维 + 分层） | `contract_kind`：`effect`(副作用契约) \| `types`(对外类型) | interface / abstract class / delegate 的 ck_symbol |
| `depa_impl` | 契约的实现 / 核心逻辑单元 | `purity`：`core`(应保持纯) \| `edge`(允许 IO 的适配器/bootstrap 层)。**这是 effect 泄漏判定的关键标注**：只有 `core` 才受 V-E1 约束 | class / method 的 ck_symbol |
| `depa_reducer` | 确定性折叠函数（Data 维，2 级事件→状态） | `deterministic`：bool（默认 true，heuristic 可降） | method 的 ck_symbol |
| `depa_projection` | 6 级投影/读模型（含 7 级 surface 归并为 `grade` 区分） | `rebuildable`：bool | class / method / 物化视图定义的 ck_symbol |
| `depa_fact_source` | 事实源阶梯上的数据节点 | `grade`：int 1-7；`grade_id`：string（`authoritative_fact` \| `domain_canonical_event` \| `runtime_control_fact` \| `append_only_journal` \| `checkpoint_snapshot` \| `derived_projection_cache` \| `surface_view`，与 fact-source-truth.md §1 的 7 级 id 一字不差）；`expected_owner`：string（预期唯一写入者的 depa 实体 id 或 symbol_id） | 字段 / 属性 / 存储 relation 声明处的 ck_symbol |
| `depa_runtime_param` | `fn(runtime,input,config)` 三参数归位结论（runtime-explicitness 决策表的输出） | `role`：`runtime` \| `input` \| `config`；`runtime_facet`：string（role=runtime 时细分：`effect_contract` \| `app_resource` \| `mutable_inner_ctx` \| `frozen_outer_ctx` \| `options`，即决策表 #1-#5）；`declared_type_id`：参数类型符号的 symbol_id | 参数（或参数类型字段）的 ck_symbol |
| `depa_runtime_carrier` | runtime 数据载体类型（runtime-paradigm §4 不变量①的判定对象） | `shape`：`flat` \| `grouped` \| `nested` \| `reactive` \| `faceted`（谱系 A-E，informational） | runtime struct/record/class 的 ck_symbol |
| `depa_entry` | capsule 唯一入口（run_<capsule> 等价物） | `entry_kind`：复用 ck_entry_point.kind 词表（public_api/http_route/mcp_tool/main） | 入口 method 的 ck_symbol（与 ck_entry_point 行对齐） |
| `depa_effect_api` | 白名单里的外部副作用 API（§4） | `target_pattern`、`category`、`direction`：`read` \| `write` \| `both` | 无 ck 锚（仓外符号），symbol_id 留空，靠 `target_pattern` 匹配 ck_external_call |
| `depa_violation` | 一次红灯命中（§3 的产物） | `rule_id`（V-E1…）、`verdict`：`GAP` \| `BLOCKED`、`dimension`：`data`\|`effect`\|`processor`\|`layering`\|`fact_source`、`message`、`evidence_json`（path:line 数组，见 §5） | 主证据符号的 ck_symbol |

> 不在 MVP：`depa_mailbox` / `depa_actor`（proposal 非目标：Actor 维动态语义第一版不做）。类型名先占坑不定义，避免半成品词表。
> Processor 维的 `depa_dispatcher`（枚举注册表）静态判定信号弱（if/elif vs 注册表需要语义分析），MVP 只保留 violation 词表位（V-P*），不建 Type。

---

## 2. 关系清单

全部经 `DefineRelationAsync` 定义，两端均为 depa_* OM 实体。

### 2.1 结构关系（正向描述"应然结构"）

| 关系 | from → to | 语义 | 数据来源 |
|------|-----------|------|----------|
| `capsule_contains` | depa_capsule → depa_* 任意 | capsule 成员归属 | root_path 前缀 join ck_file.path |
| `capsule_exposes` | depa_capsule → depa_entry | capsule 的公开入口。**基数期望 = 1**（多入口是 V-L4 信号，经计数查询判，不靠关系本身） | ck_entry_point ∩ capsule 成员 |
| `capsule_depends_on` | depa_capsule → depa_capsule | capsule 间依赖（应单向无环） | 聚合成员间 IMPORTS/CALLS 跨 capsule 边 |
| `contract_implemented_by` | depa_contract → depa_impl | 契约的实现 | ck_edge{kind: IMPLEMENTS \| METHOD_IMPLEMENTS} |
| `impl_uses_contract` | depa_impl → depa_contract | core 逻辑经契约产生副作用（合规路径） | impl 的参数/字段类型是 contract 符号（ck_edge ACCESSES + 签名类型 join） |
| `entry_delegates_to` | depa_entry → depa_impl | 入口编排调的核心逻辑 | ck_edge{kind: CALLS} |
| `runtime_carries` | depa_runtime_carrier → depa_runtime_param | runtime 载体的字段归位 | carrier 字段符号（HAS_PROPERTY）+ 归位判定 |
| `fn_takes` | depa_impl → depa_runtime_param | 函数的三参数归位 | 签名解析 |
| `projection_derived_from` | depa_projection → depa_fact_source | 投影的单一上游（衍生链，单向） | reducer/投影函数的读路径（ACCESSES read）+ 标注 |
| `reducer_folds` | depa_reducer → depa_fact_source | reducer 折叠哪个事件源产出状态 | 同上 |
| `fact_written_by` | depa_fact_source → depa_impl | **实测**写入者（每 grade1-3 节点期望恰好 1 条；>1 = V-D1） | ck_edge{kind: ACCESSES, evidence 含 write} 上溯 |

### 2.2 违反信号关系（负向，"存在即红灯"，由 §3 检测查询物化）

| 关系 | from → to | 对应红灯 |
|------|-----------|----------|
| `effect_leaks_through` | depa_impl(purity=core) → depa_effect_api | V-E1：核心逻辑绕过 contract 直接调 IO |
| `backwrites` | depa_projection → depa_fact_source | V-S1：投影/视图反写高级别上游 |
| `violates` | depa_violation → depa_*（任意被告实体） | 所有红灯：violation 挂到主体 |
| `evidenced_by_edge` | （不建 OM 边）violation 的 `evidence_json` 属性内嵌 ck_edge 五元组 | ck_edge 行非 OM 实体，证据走属性不走边（见 §5） |

### 2.3 用 ExistentialRule 表达的正向必备（Mode=Check）

| 规则名 | ForEach | Exists | 缺失时语义 |
|--------|---------|--------|-----------|
| `capsule_must_expose_entry` | depa_capsule | `capsule_exposes` → depa_entry (Out) | capsule 无任何入口 → GAP（未 capsule 化） |
| `contract_must_have_impl` | depa_contract where contract_kind=effect | `contract_implemented_by` → depa_impl (Out) | 空契约（声明了副作用契约无实现）→ GAP |
| `projection_must_have_upstream` | depa_projection | `projection_derived_from` → depa_fact_source (Out) | 投影找不到单一上游 → BLOCKED（无法定级） |
| `factsource_must_have_writer` | depa_fact_source where grade≤3 | `fact_written_by` → depa_impl (Out) | 1-3 级节点找不到写入者 → BLOCKED（观测不足） |
| `violation_must_have_subject` | depa_violation | `violates` → depa_node (Out) | 物化管线自检：violation 不许悬空 |

`CheckExistentialRulesAsync` 的 `ExistentialViolation` 输出直接映射成 verdict=BLOCKED/GAP 的报告行。

---

## 3. 违反信号 → 判定输入与 OM 表达（MVP 8 条红灯）

统一模式：**Datalog 检测查询（读 ck_* + depa_* 标注）→ 物化 `depa_violation` 实体 + `violates` 边 + 专用信号边**。violation catalog 编号沿用维度前缀。

| # | rule_id | 红灯（violation-catalog 出处） | 判定输入（需要的 ck_*/标注） | 检测逻辑（静态） | OM 表达 |
|---|---------|------|------|------|------|
| 1 | **V-E1** | 核心逻辑直接 IO（B 组"核心逻辑里直接 IO"） | ① `depa_impl{purity:core}` 标注；② ck_edge{CALLS}（仓内）+ **ck_external_call**（仓外，§4）；③ effect API 白名单 | core 符号（含其 CONTAINS 闭包内私有方法）的外呼命中白名单 category ∈ {file_io,network,db,process,console,env}，且调用点类型不是任何 `depa_contract` 的实现 | 物化 violation(rule=V-E1, verdict=GAP) + `effect_leaks_through` 边 |
| 2 | **V-E2** | 隐式全局依赖（B 组"隐式全局依赖"+ runtime-explicitness"core 自己发现依赖"） | ① ck_edge{ACCESSES}；② ck_symbol.kind=field + signature 含 `static`（需索引器暴露 static/readonly 修饰，缺则降级为 heuristic）；③ core 标注 | core 符号 ACCESSES 一个 static 可变字段，且该字段不在自身 capsule 内 / 或命中 `Environment.GetEnvironmentVariable` 类白名单 category=env | violation(V-E2, GAP)；证据 = ACCESSES 边 path:line |
| 3 | **V-D1** | 多写入者（A 组 / 事实源规则①） | ① `depa_fact_source{grade≤3, expected_owner}`；② `fact_written_by` 边（由 ACCESSES-write 上溯物化） | `count(fact_written_by) > 1`，或唯一写入者 ≠ expected_owner | Datalog 聚合物化 violation(V-D1, GAP)，message 列全部写入者；ExistentialRule 无法表达计数，不用它 |
| 4 | **V-S1** | 投影反写源事实（E 组"投影算完反写源事实"/规则②⑤） | ① depa_projection + depa_fact_source(grade≤2) 标注；② ACCESSES-write 边（投影符号或其闭包 → 上游字段）；③ CALLS 边到上游 owner 的写方法 | 投影符号存在到 grade≤2 节点的写路径（1 跳 write ACCESSES，MVP 不追传递闭包） | violation(V-S1, GAP) + `backwrites` 边 |
| 5 | **V-F1** | 函数对象塞 config（F 组"函数对象塞 config"） | ① `depa_runtime_param{role:config}` 归位标注；② 参数类型符号 + 其字段（HAS_PROPERTY）+ 字段 signature | config 参数类型的任一字段 signature 匹配 `Func<`/`Action<`/`delegate`/`*Callback`（C# 起步词表，语言后续扩展） | violation(V-F1, GAP)；纯 ck_symbol.signature 文本判定，静态可靠 |
| 6 | **V-F2** | runtime 容器带业务逻辑（F 组"业务逻辑写在 runtime dataclass 方法里"/ runtime-paradigm 不变量①） | ① `depa_runtime_carrier` 标注；② HAS_METHOD 边 + 方法符号 kind/signature；③ 方法的 CALLS 出边 | carrier 类型拥有非 getter/ctor 方法，且该方法有 ≥1 条 CALLS 出边（排除纯访问器；heuristic，confidence≤0.8） | violation(V-F2, GAP) |
| 7 | **V-L1** | 外部触 internals（F 组"外部 import 触及 internals.*"/capsule-protocol §5） | ① depa_capsule.internals_path glob；② ck_edge{IMPORTS \| CALLS \| ACCESSES} + 两端 ck_file.path | 边的 from 属于 capsule A、to 的 path 命中 capsule B 的 internals glob（A≠B） | violation(V-L1, GAP)；证据 = 每条越界边 |
| 8 | **V-L3** | contract 反向依赖 logic（F 组"contract 与 logic 反向依赖"） | ① depa_contract 标注（或 config 声明的 contract 包路径）；② IMPORTS 边 | contract 符号所在文件 IMPORTS 任一 depa_impl 所在文件 / logic 包路径 | violation(V-L3, GAP) |

**MVP 明确不做（记入设计留白）**：V-D2 状态不可重建（需运行时语义）、V-P1 if/elif 字符串分发（需语句级 AST，ck_* 只到符号粒度）、Actor 维全部、事实源 3/4/5 级 gate-live 类红灯（需控制流分析）。这些在报告里对应维度输出 `BLOCKED + 原因=静态观测不足`，不假装 PASS。

**物化管线**：`depa_scan`（新工具/扩展方法）顺序执行 ①同步标注（§4 配置 + heuristic）→ ②物化结构关系（§2.1）→ ③跑 8 条检测 Datalog、upsert violation（id 含 rule+证据键，幂等）→ ④`CheckExistentialRulesAsync` 收正向缺失 → ⑤输出 conformance 报告。reindex 后全量重跑（violation 可重建，属 6 级投影语义——本体系自身遵守 DEPA：depa_violation 是 ck_* 的衍生投影，不反写 ck_*）。

---

## 4. effect API 标注机制

### 4.1 白名单：内置起步 + 用户扩展

配置文件 `depa-effects.json`（仓根或 `.codument/` 下，数组按序匹配，先命中先赢；内置表编译进 Om.CodeKnowledge，用户文件追加/覆盖）：

```jsonc
{
  "version": 1,
  "effects": [
    // pattern：外部目标的 FQN glob（* 单段，** 任意段）
    { "pattern": "System.IO.**",                       "category": "file_io",  "direction": "both"  },
    { "pattern": "System.IO.File.Read*",               "category": "file_io",  "direction": "read"  },
    { "pattern": "System.Net.Http.**",                 "category": "network",  "direction": "both"  },
    { "pattern": "System.Data.**",                     "category": "db",       "direction": "both"  },
    { "pattern": "Microsoft.Data.Sqlite.**",           "category": "db",       "direction": "both"  },
    { "pattern": "System.Diagnostics.Process.**",      "category": "process",  "direction": "both"  },
    { "pattern": "System.Console.**",                  "category": "console",  "direction": "write" },
    { "pattern": "System.Environment.GetEnvironment*", "category": "env",      "direction": "read"  },
    { "pattern": "System.Random.**",                   "category": "nondeterminism", "direction": "read" },
    { "pattern": "System.DateTime.Now",                "category": "nondeterminism", "direction": "read" },
    // 用户扩展：框架级契约豁免（命中即视为"经契约"，不算泄漏）
    { "pattern": "Cozo.DotNet.ICozoOmStore.**",        "category": "exempt_contract" }
  ]
}
```

- `category` 词表：`file_io | network | db | process | console | env | threading | nondeterminism | exempt_contract`。前 7 类参与 V-E1；`nondeterminism` 只参与 reducer 确定性降级（depa_reducer.deterministic=false）；`exempt_contract` 是豁免通道。
- 每条内置/用户 pattern 在 OM 里物化为一个 `depa_effect_api` 实体（§1），使"白名单本身"也可查询、可在 wiki 呈现。

### 4.2 外部调用摘要通道：`ck_external_call`

P0（deepen-llm-wiki-code-graph）口径是**仓外调用不落 ck_edge、只计数**——这对 wiki 够用，但 DEPA 的 V-E1 恰恰要看"仓内符号 → 仓外 IO API"这条边。不推翻 P0 口径（避免 ck_edge 被 BCL 噪声淹没），而是给 DEPA 增设一张**低保真摘要关系**：

```text
:create ck_external_call {
    caller_id, target_key =>          # caller_id = ck_symbol.symbol_id；target_key = 归一化外部 FQN
    count default 0,                  # 该 caller 对该目标的调用次数
    category default "",              # 索引期按当时加载的白名单预分类；空 = 未分类
    first_file_id default "",         # 首个调用点（证据用，满足 path:line 纪律）
    first_line default 0,
    resolver default ""               # 与 ck_edge.resolver 同词表
}
```

设计要点：

1. **落点在索引器**：Roslyn/解析器在丢弃仓外 CALLS 目标前，多写一行 ck_external_call（`target_key` = 目标方法/属性 FQN，泛型擦除、重载合并到方法名粒度）。成本 = 每 (caller, 外部目标) 一行，远小于逐边落 ck_edge。
2. **category 双阶段**：索引期用内置白名单预填（快路径）；`depa_scan` 期再用"内置+用户"合并白名单重匹配 `target_key`（用户改了 depa-effects.json 无需 reindex，只重跑 scan）。匹配结果不回写 ck_external_call（保持观测层只读），而是在检测查询里现场 join `depa_effect_api.target_pattern`。
3. **证据妥协点（如实呈现）**：只存 first 调用点，同 caller 对同一 API 的第 2..n 个调用点不可逐点定位。violation 的 evidence 写 `first_file:first_line (+N more calls)`。若后续需要逐点证据，升级路径是把 key 扩成 `{caller_id, target_key, file_id, line}`——schema 预留，不 MVP。
4. **物化为图事实**：检测查询 `caller ∈ core 闭包 ∧ ck_external_call.target_key ~ effect_api.pattern ∧ category ∉ {exempt_contract}` 命中后，建 `effect_leaks_through`(depa_impl → depa_effect_api) OM 边，边 props 携带 `{target_key, count, first_path, first_line}`。仓内 IO 封装（如项目自己的 FileHelper 直调 File.*）由 V-E1 对 FileHelper 报告，core 调 FileHelper 属仓内 CALLS 边，MVP 不做传递闭包泄漏（记为已知限制，deep 档位候选）。

### 4.3 角色标注来源（capsule/contract/core/fact_source 从哪来）

白名单解决"什么是 effect"，还需要"什么是 core/contract"。三通道，优先级从高到低，全部落 `assigned_by`：

1. **配置文件 `depa-map.json`**（与 depa-effects.json 同目录）：声明 capsule 根路径、internals glob、contract 包路径、runtime carrier 类型名、fact_source 定级表（`{symbol_or_path, grade, expected_owner}`）。人工判定的真源，`assigned_by=config`。
2. **代码内注解**（可选增强）：C# Attribute（`[DepaContract]`/`[DepaCore]`/`[DepaFactSource(Grade=1)]`）——索引器读 attribute 落 ck_symbol.signature，scan 期解析。`assigned_by=manual`。
3. **启发式兜底**：目录名 `Contracts/`→contract、`Internals/`→internals、interface+被注入→effect contract、名含 Runtime/Context 的 record→carrier。`assigned_by=heuristic, confidence≤0.7`；heuristic 结论只产 GAP 不产 BLOCKED 升级，报告标注可信度。

---

## 5. 判定纪律：PASS / GAP / BLOCKED + 证据字段

沿用 depa-expert 的裁决词汇与"观测优先于猜测"不变量，落成结构化字段：

### 5.1 verdict 语义（与 depa-expert protocols 对齐）

| verdict | 含义 | 在本体系里何时产生 |
|---------|------|--------------------|
| `PASS` | 该检查项扫过、无命中 | 检测查询空结果 + 输入完备（所需标注/ck 数据齐全） |
| `GAP` | 命中红灯，有 path:line 证据 | depa_violation 物化成功 |
| `BLOCKED` | 无法判定：观测不足/标注缺失 | ExistentialRule Check 的缺失类（§2.3）、或检测前置输入缺失（如无任何 depa_map 标注、索引器未暴露 static 修饰） |

关键纪律：**BLOCKED ≠ PASS**。每个维度报告 8 条规则各自的 verdict，缺输入的规则显式 BLOCKED 并说明缺什么（例：`V-E2 BLOCKED: ck_symbol.signature 未携带 static 修饰，需索引器升级`）。

### 5.2 depa_violation 的证据字段（`evidence_json` 属性 schema）

```jsonc
{
  "rule_id": "V-E1",
  "verdict": "GAP",
  "dimension": "effect",
  "subject": { "entity_id": "depa:impl:...", "symbol_id": "...", "sym_key": "..." },
  "evidence": [
    // 每条必有 path:line；kind 说明证据类型
    { "kind": "external_call", "path": "src/Foo/Core.cs", "line": 88,
      "detail": "CALLS System.IO.File.WriteAllText (count=3, +2 more call sites)" },
    { "kind": "annotation",    "path": ".codument/depa-map.json", "line": 12,
      "detail": "purity=core assigned_by=config" }
  ],
  "confidence": 1.0,          // = min(所有输入标注的 confidence)
  "detected_at": "<scan 时间戳>",
  "scan_commit": "<ck_repo.commit>"   // 证据与代码版本绑定，reindex 后过期重算
}
```

规则：① 无 path:line 的命中**不得**物化为 violation（宁可 BLOCKED）；② heuristic 输入参与的 violation，confidence 随之衰减并在报告排序中后置；③ violation id 由 `rule_id + subject.sym_key + 证据主键` 决定，重复 scan 幂等 upsert，消失的命中在 scan 尾声按 `scan_commit` 清理（violation 是可重建投影，见 §3 末）。

### 5.3 报告聚合（喂 depa_conformance 工具）

`depa_conformance` 输出按四维+分层+事实源分组：每组 = 规则清单 × verdict × violation 列表（按 confidence 降序）× BLOCKED 原因。`health_score` 只做展示层加权（GAP 数/规则覆盖率），**不合并四维为单一分数作裁决**（tao/depa-paradigm：四维分别裁决）。`fact_grade_map` = `FindByTypeAsync("depa_fact_source")` + `fact_written_by`/`projection_derived_from` 邻接导出。

---

## 6. 落地顺序建议（供后续 track 拆分参考）

1. schema 先行：depa_* Type/Relation/ExistentialRule 定义 + depa-effects.json / depa-map.json 解析（无检测也可手工标注查询）。
2. ck_external_call 索引器改造（依赖 P0 索引管线，是唯一动观测层的项）。
3. 静态最可靠的三条先做：V-F1（纯签名文本）、V-L1（纯路径 glob + 边）、V-E1（external_call + 白名单）。
4. 依赖标注较重的后做：V-D1/V-S1（需 fact_source 定级表）、V-E2/V-F2（需索引器 signature 增强/启发式调优）、V-L3。
5. 验收对齐 mission 成功判据：对 Om.Core（合规样本）+ 一个故意违规样本跑出可区分、带 path:line 的结论。

---

# Track 补充设计（G6 落地裁定）

- 本 track 实现映射设计 §3 全表（8 条 V-*）、§5 判定纪律与 §5.3 报告聚合；§1/§2/§4 已由 G5 track（add-llm-wiki-depa-ontology，已归档）落地。
- DepaScanPipeline ③段插在 ②结构物化与 ④规则 Check 之间；violation id = rule_id + subject.sym_key + 证据主键 hash；scan 尾按 scan_commit 清理消失的命中。
- 检测查询实现语言：优先 Datalog（ck_*+depa_* join，经 OM store RunAsync），聚合/计数类（V-D1）可 .NET 侧收集后判定——以简单正确为准，记 findings。
- 工具面：三工具进 LlmWikiToolRunner（14→17）；depa_conformance 参数 scanFirst（默认 true，先跑 DepaScanAsync 再聚合）、dimension 过滤、mapPath/effectsPath 透传；工具层零业务逻辑（聚合在 Om.Depa 报告模型中）。
- 最小公开面：DepaScanAsync 签名不变（内部扩展 ③段）；新增公开仅报告聚合查询 API（GetConformanceReportAsync/GetFactGradeMapAsync/GetHealthScoreAsync + result records）；检测器全 internal。
- 验收双样本 fixture 放 llm-wiki tests（走真实索引管线 Roslyn 路径，保证 ck_external_call/标注全链路真实）。
