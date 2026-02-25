---
knowledge_plane: domain
doc_role: canonical
status: active
context: om-type-system
last_verified: 2026-07-01
---

# OM 类型系统

OM 类型系统是在 Cozo stored relations 之上、ontology/rule/action 等高层能力之下的一层 schema 语言。它给实体、属性和关系提供可演化的名字、类型约束、继承组合和多态查询语义。Bun 与 .NET 实现应共享同一套底层语义。

## Mental Model

类型系统回答四个问题：

- 一个 entity 属于哪个 canonical type。
- 这个 type 可见哪些 attribute，以及每个 attribute 的值类型、必填性和描述。
- 两个 entity 能否通过某个 relation 连接。
- 当 type、relation、attribute 改名时，旧数据和旧调用如何继续读写。

Cozo stored relations 是事实源。OM API 负责把用户输入解析成 canonical names，执行类型校验，并把兼容行为投影到读写查询上。

## Core Concepts

`Type` 是实体分类和属性可见性的边界。类型可以有一个 parent type，形成单继承树；`isSubtypeOf(child, parent)` 对自身也为 true。

`Mixin` 是可复用属性包。mixin 不是父类型，不参与 subtype 判断；它只把自己的属性定义贡献给使用它的 type。

`Attribute` 是 type 或 mixin 下的属性定义。属性定义包括 value type、required 标记和可选 description。支持的值类型包括常规标量、引用、JSON、Validity 等实现已登记类型。

`Relation` 定义 named edge 的 endpoint type。directed relation 按 from/to 校验；undirected relation 允许 endpoint 类型反向匹配。

`Alias` 把旧名或替代名映射到 canonical name。type alias、relation alias、attribute alias 分开存储和解析，并检测 cycle。

## Type Hierarchy

类型继承是单父树：

- 定义 parent type 时，parent 必须存在。
- 不能把 type 的 parent 设为自己，也不能形成循环。
- 查询 ancestors 时返回 nearest-to-farthest parent chain。
- 查询 descendants 时返回该 type 下的所有后代。
- type hierarchy view 暴露 roots、parent、children、mixins 和 descriptions。

重新定义已有 type 时，如果调用没有显式传入 parent/mixins，已有 parent/mixins 应保留。这让 schema 描述或增量迁移可以安全更新 description，而不会意外切断继承或 mixin 连接。

## Effective Attributes

一个 type 的 effective attribute definitions 按优先级合成：

1. mixins，优先级最低。
2. far ancestors。
3. near ancestors。
4. self，优先级最高。

后出现的同名 attribute 覆盖前面的定义。覆盖受到安全约束：

- 子类型可以把 inherited optional attribute 收紧为 required。
- 子类型不能把 inherited required attribute 放松为 optional。
- 子类型不能改变 inherited attribute 的 value type。

Description 跟随 attribute definition 一起合成，nearest/self definition 的 description 胜出。

## Aliases And Compatibility

所有 public write/read 路径先解析 canonical names：

- type aliases 用于 entity type、hierarchy、query 和 validation。
- relation aliases 用于 link/unlink/query relation name。
- attribute aliases 用于 property read/write、validation 和 effective definitions lookup。

当旧数据已经用 alias attribute name 存在时，读取 canonical attribute 会 fallback 到 alias-stored row；如果 canonical row 和 alias row 同时存在，canonical row wins。validation 也按 canonical property bag 判断 required attributes。

Alias cycles 是错误，不能静默选择其中一个名字。

## Entities And Properties

创建或 upsert entity 时，type name 先解析为 canonical type，然后必须确认 canonical type 已定义。未知 type 是 schema 错误。

设置 property 时：

- entity 必须存在。
- attr name 解析为 canonical attr。
- canonical attr 必须在 entity type 的 effective attributes 中可见。
- value 必须匹配 attribute value type。
- Validity attribute 接受可规范化为 Cozo validity value 的输入，并作为 Cozo validity value 存储。

属性读默认读取当前有效值；temporal read 读取指定 as-of 时间的有效值。

## Relations

定义 relation 时，relation name 和 endpoint type names 会 canonicalize。linking entities 时：

- relation 必须已定义。
- endpoints 的 actual entity types 必须是 relation endpoint types 的 subtype。
- undirected relation 允许 endpoint 顺序反转。

Relation alias 不改变 relation 的语义，只改变入口名字。

## Polymorphic Queries

按 type 查询默认是 polymorphic：目标 type 自身及所有 descendants 都在查询集合中。`exact` 模式只匹配 canonical type 自身。

聚合查询遵循同样的 type 集合规则，支持 `sum`、`avg`、`min`、`max`、`count`。聚合 attribute name 先 canonicalize。

## Compatibility Contract

Bun 和 .NET 的 OM 类型系统应在以下行为上保持一致：

- schema facts 使用相同 stored relations。
- canonicalization、alias cycle detection 和 alias fallback 一致。
- effective attributes 的合成顺序一致。
- inherited attribute override safety 一致。
- unknown entity type rejection 一致。
- polymorphic query 和 exact query 的 type set 一致。
- Validity attribute 的语义一致。
