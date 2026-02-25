---
knowledge_plane: global
doc_role: guide
status: active
last_verified: 2026-07-01
---

# OM 类型系统维护指南

本文面向需要修改 `cozo-lib-bun` 或 `cozo-lib-dotnet` 中 OM 类型系统内核的开发者。对外语义真源在 [OM 类型系统](../../../modeling/domain/contexts/om-type-system/objects/type-system.md)；本文只解释这些语义在代码中的落点、维护边界和同步方法。

## 实现边界

类型系统位于 raw Cozo stored relations 与更高层 OM 能力之间：

- stored relations 是 schema facts 的可执行事实源。
- type logic 负责 canonicalize 名字、合成 effective definitions、校验 schema 演化，并暴露 hierarchy inspection。
- entity/relation logic 通过 type logic 做 entity type validation、property read/write、relation endpoint check 和 polymorphic query。
- ontology、rules、permissions、actions、computed properties、existential reasoning 是上层消费者，不应重新定义类型系统语义。

不要引入第二套内存事实源。可以有局部 cache，但 cache 必须限定在一次操作内，或有清晰失效边界。

## 主要代码入口

| Concern | Bun | .NET |
|---------|-----|------|
| type hierarchy、mixin、alias、attribute | `cozo-lib-bun/cozo-om.js` | `cozo-lib-dotnet/src/Om.Core/Logic/TypeLogic.cs` |
| entity type validation、property、polymorphic query | `cozo-lib-bun/cozo-om.js` | `cozo-lib-dotnet/src/Om.Core/Logic/EntityLogic.cs` |
| relation endpoint validation | `cozo-lib-bun/cozo-om.js` | `cozo-lib-dotnet/src/Om.Core/Logic/RelationLogic.cs` |
| Validity conversion | `cozo-lib-bun/cozo-om.js` | `cozo-lib-dotnet/src/Om.Core/Internals/OmConvert.cs` |
| public facade/model shape | `cozo-om.js` exports | `CozoOm.cs`、`Contracts/Models/OmModels.cs` |
| parity tests | `cozo-lib-bun/__tests__/om-*.test.js` | `cozo-lib-dotnet/tests/Program.cs` |

## 共享 Stored Relations

这些 relations 是 Bun 和 .NET 共享的内核契约：

- `om_type(name => description, parent_type)`
- `om_mixin(name => description)`
- `om_type_mixin(type_name, mixin_name)`
- `om_attr_def(type_name, attr_name => value_type, required)`
- `om_attr_desc(type_name, attr_name => description)`
- `om_rel_def(rel_name => from_type, to_type, directed)`
- `om_rel_desc(rel_name => description)`
- `om_alias_type(alias => canonical)`
- `om_alias_rel(alias => canonical)`
- `om_alias_attr(type_name, alias_attr => canonical_attr)`
- `om_entity(id => type_name, label)`
- `om_property(entity_id, attr_name, valid_time => value, tx_time)`

schema migration 可以增加兼容处理，但不能悄悄改变这些 relation 的语义。任何语义变化都要两边同步测试。

## 必须保持的语义不变量

- `defineType` 在 omitted options 时保留已有 parent/mixins。
- parent type 必须存在；循环继承必须拒绝。
- mixin 贡献 attribute，但不是 parent type。
- effective attributes 合成顺序固定为：mixins、far ancestors、near ancestors、self。
- child definition 可以把 optional 收紧为 required，但不能放松 required，也不能改变 value type。
- type/relation/attribute aliases 都要 canonicalize，并检测 cycle。
- canonical property row 优先；alias-stored row 只作为 fallback。
- entity create/upsert 必须拒绝未知 canonical type。
- directed relation 按 from/to 校验；undirected relation 允许反向 endpoint typing。
- polymorphic query 默认包含 descendants；exact mode 只匹配自身。
- `Validity` attribute value 要存成 Cozo validity value，不是普通 string。

## 修改流程

1. 先从对外建模文档判断变更是否改变 public type-system semantics。
2. 如果改变语义，同一轮补 Bun 和 .NET 两边测试。
3. 把改动放到对应边界：
   - schema/hierarchy/alias/effective attribute 逻辑放 type logic；
   - entity existence/property/query 行为放 entity logic；
   - endpoint validation 放 relation logic；
   - value conversion 放 conversion helpers。
4. .NET 保持 DEPA 边界：effects 通过 `ICozoOmStore`，不要绕过 runtime/store。
5. Bun 优先使用已有 DSL helpers 构造 CozoScript。
6. 行为变化时更新 `docs/modeling/.../type-system.md`；维护入口或代码 ownership 变化时更新本文。

## 测试矩阵

开发时先跑 .NET 聚合测试入口：

```bash
dotnet run --project cozo-lib-dotnet/tests/Cozo.DotNet.Om.Tests.csproj
```

再跑 Bun 类型系统相关测试：

```bash
cd cozo-lib-bun
bun test __tests__/om-type-hierarchy.test.js \
  __tests__/om-attr-inheritance.test.js \
  __tests__/om-mixin.test.js \
  __tests__/om-alias-resolution.test.js \
  __tests__/om-validity-attr.test.js \
  __tests__/om-polymorphic-query.test.js \
  __tests__/om-rel-inheritance.test.js \
  __tests__/om-description.test.js \
  __tests__/om-backward-compat.test.js
```

收口 parity 工作前，再跑：

```bash
dotnet build cozo-lib-dotnet/Cozo.DotNet.csproj -tl:off
dotnet build cozo-lib-dotnet/tests/Cozo.DotNet.Om.Tests.csproj -tl:off
cd cozo-lib-bun && bun test __tests__/om-*.test.js
```

## 常见故障模式

Alias 改动容易破坏 legacy reads。改 attribute resolution 时，一定同时测 canonical row 和 alias-stored row。

Effective attribute 改动很容易只覆盖 direct parent，却漏掉 mixin 或 far ancestor。测试要同时覆盖 mixin、grandparent、parent、self precedence。

Type redefinition 很容易变成破坏性操作。omitted options 表示保留已有结构；显式 empty mixins 或 null parent 才表示调用者要改变结构。

Validity value 需要 expression-based Cozo write。不要把它当普通 JSON/string 参数写入。

Polymorphic query 的常见错误是先计算 type set 再 resolve alias，或者忘记 exact mode。
