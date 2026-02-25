# 变更：审计并修复 .NET OM 类型系统 parity

## 背景和动机

`cozo-lib-dotnet` 的 OM 层已经完成主干移植，但与 `cozo-lib-bun` 的类型系统底层语义相比仍有若干遗漏。遗漏集中在 mixin 属性、继承属性 override 限制、`Validity` 属性值、alias rename 兼容和类型重定义语义上。这些都处于本体论功能之下、Cozo relation 之上的内核层，会影响后续 ontology、规则和可视化能力的正确性。

## "要做"和"不做"

**目标:**
- 为已确认的类型系统 parity 缺口补 .NET 测试覆盖。
- 修复 `Om.Core` 中的 mixin attribute 合成和继承属性 override 校验。
- 修复 alias fallback 和 `Validity` 属性值处理。
- 暴露必要的类型系统检查 API。
- 生成 mission 可复用的 gap matrix 与验证证据。

**非目标:**
- 不重写 `.NET OM` DEPA capsule。
- 不在本 track 中重构 Bun 实现。
- 不扩展更高层 ontology/rule 语义，除非为了保持类型系统依赖正确。

## 变更内容

- 修改 `cozo-lib-dotnet/src/Om.Core/Logic/TypeLogic.cs`。
- 修改 `cozo-lib-dotnet/src/Om.Core/Logic/EntityLogic.cs`。
- 修改 `cozo-lib-dotnet/src/Om.Core/Internals/OmConvert.cs`。
- 按需修改 `cozo-lib-dotnet/src/Om.Core/CozoOm.cs` 与模型/input 类型。
- 扩展 `cozo-lib-dotnet/tests/Program.cs` 覆盖 parity 场景。

## 影响范围

- 受影响的能力：`cozo-dotnet-om`
- 受影响的代码：`cozo-lib-dotnet/src/Om.Core/**`、`cozo-lib-dotnet/tests/**`
