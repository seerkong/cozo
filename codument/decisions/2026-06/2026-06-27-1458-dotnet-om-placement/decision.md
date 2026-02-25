# C# OM 放置位置

**Durable / 长期项目决策**

## Decision

C# OM 放入现有 `cozo-lib-dotnet` 包内，代码根目录固定为 `cozo-lib-dotnet/src/Om/`，随现有 NuGet 包交付。

## Rationale

- 降低发布、依赖和版本协调复杂度。
- 当前 `cozo-lib-dotnet/Cozo.DotNet.csproj` 已包含 `src/**/*.cs`，新增 OM capsule 无需额外 compile include。
- C# OM 是现有 .NET binding 之上的本体论封装，和 `CozoDb` public API 有直接协作关系。

## Constraints

- 不为 v1 新建独立 C# OM NuGet 包。
- 后续如果要拆包，必须先说明兼容、发布、依赖边界与迁移策略。

## Source

- `archive/2026-06/2026-06-27-1458-add-dotnet-om-depa/decisions.md`

## Reference

- `decision://dotnet-om-placement`
