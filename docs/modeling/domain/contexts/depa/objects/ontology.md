---
knowledge_plane: domain
doc_role: canonical
status: active
context: depa
last_verified: 2026-07-06
---

# depa_* 判读本体

解释层本体建在 OM 类型系统之上：根类型 `depa_node` + 11 个子类型 + 14 条关系。每个实体是一次**判断**而非事实，所以根类型携带三件公共语义：

- **锚定**：`symbol_id`（当次索引锚）+ `sym_key`（跨重建再锚定键）+ `path`/`line`（首要证据位置）——判断挂在观测层符号上，重索引后靠 sym_key 找回。
- **provenance**：`assigned_by ∈ {manual, config, heuristic, llm}`——谁下的判断。
- **confidence**：0–1，heuristic/llm 判断必填——判断强度不冒充确定性。

## 11 个子类型（按判读维度分组）

| 类型 | 语义 |
|------|------|
| `depa_capsule` | 自足模块单元（root_path 前缀锚定成员、internals_path 划内部区、entry_count 计算列） |
| `depa_entry` | capsule 入口点（entry_kind 沿用 ck_entry_point 词表） |
| `depa_contract` | effect/types 契约（contract_kind 二分） |
| `depa_impl` | 契约实现/核心逻辑（`purity`: core 必须保纯 / edge 允许 IO 的适配层） |
| `depa_reducer` | 确定性折叠函数（事件→状态，Data 维） |
| `depa_projection` | 派生读模型（rebuildable 承诺可重建） |
| `depa_fact_source` | 事实源阶梯节点（`grade` 1–7，词表 authoritative_fact → surface_view；`expected_owner` 声明期望单写者）——分级判据真源见 [rubrics/fact-source-truth.md](../../../../../../cozo-lib-dotnet/src/Om.Depa/rubrics/fact-source-truth.md) |
| `depa_runtime_param` | fn(runtime, input, config) 参数安放裁决（role + runtime_facet + declared_type） |
| `depa_runtime_carrier` | runtime 数据载体类型（shape 词表） |
| `depa_effect_api` | 白名单化的外部副作用 API（FQN glob + category + direction） |
| `depa_violation` | 一次红灯命中（规则 id、verdict、证据；见 [conformance-report.md](conformance-report.md)） |

## 14 条关系（判读图的骨架）

结构声明类：`capsule_contains`、`capsule_exposes`（期望基数 1，多入口即 V-L4 信号）、`capsule_depends_on`（应无环单向）、`contract_implemented_by`、`impl_uses_contract`（core 经契约生效——合规路径本身）、`entry_delegates_to`、`runtime_carries`、`fn_takes`、`projection_derived_from`（投影单上游）、`reducer_folds`、`fact_written_by`（grade 1–3 期望恰一个写者，多写即 V-D1）。

违规观测类：`effect_leaks_through`（V-E1：core 绕过契约直接 IO）、`backwrites`（V-S1：投影反写高阶上游）、`violates`（violation 挂到主体实体）。

## Invariants

- 解释层**只加判断不加事实**：删除全部 depa_* 行不损失任何代码观测。
- 关系的期望基数（单入口、单写者、单上游）不由 schema 强制，而由检测器把违反计为信号——本体宽容落数据，判读收紧下结论。
