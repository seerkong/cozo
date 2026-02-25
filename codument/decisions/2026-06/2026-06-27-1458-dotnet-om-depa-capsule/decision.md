# C# OM 使用 DEPA Capsule

**Durable / 长期项目决策**

## Decision

C# OM 内部采用 DEPA capsule 组织：`Contracts/`、`Runtime/`、`Inputs/`、`Logic/`、`Support/`、`Internals/` 分层，`CozoOm.cs` 只作为薄 facade。

## Rationale

- C# 版应追求 Node OM 行为 parity，但不逐行翻译大型 `cozo-om.js`。
- `Logic/` 层通过 `ICozoOmStore` 访问 CozoScript，不直接依赖 `CozoDb` 或 native interop。
- `Runtime/` 是依赖与选项的数据载体，不承载本体业务方法。
- 这种组织让事实源边界、effect 边界、测试替身和后续扩展更清晰。

## Constraints

- 业务逻辑不得沉积到 `CozoOm` facade。
- 直接 `CozoDb` 调用只能位于 `Support/` 或更外层集成代码。
- 新增 OM 能力应优先按 input record + logic function + facade delegate 的形态扩展。

## Source

- `archive/2026-06/2026-06-27-1458-add-dotnet-om-depa/decisions.md`
- `archive/2026-06/2026-06-27-1458-add-dotnet-om-depa/guidance/depa-capsule-layout.md`

## Reference

- `decision://dotnet-om-depa-capsule`
