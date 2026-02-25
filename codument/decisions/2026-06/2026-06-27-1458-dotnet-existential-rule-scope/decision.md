# C# 存在规则 v1 范围

**Durable / 长期项目决策**

## Decision

C# OM 的存在规则保持 Node OM v1 的 head 范围：仅支持 `exists { rel, direction?, toType }`，即「存在关系边 + 目标实体」。

属性存在性不进入 existential rule head，继续由 required attribute 与 `ValidateEntityAsync` 表达。

## Rationale

- 该范围覆盖主数据场景中最常见的存在约束。
- 属性存在性已有更直接的约束机制，重复建设会造成语义漂移。
- C# OM 应与 Node OM 共享同一组 Cozo OM metadata relation 语义。

## Constraints

- 不在 C# 侧单独扩展属性存在性 head。
- 未来若扩展复合 head 或属性 head，必须同时修订 Node/C# 行为契约和 schema snapshot/diff/rollback 范围。

## Source

- `archive/2026-06/2026-06-27-1458-add-dotnet-om-depa/decisions.md`
- `decision://existential-rules-v1-scope`

## Reference

- `decision://dotnet-existential-rule-scope`
