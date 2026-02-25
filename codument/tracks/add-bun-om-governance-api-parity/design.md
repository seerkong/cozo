# 上下文

目标是把 Bun OM 的治理 API surface 与现有 .NET OM 对齐。Bun 的 alias storage/resolution 和 temporal object storage 已存在，因此本变更应是小而完整的 facade 补齐，而不是重写 schema kernel。

## 方案概览

1. Alias authoring
   - `defineTypeAlias(runner, alias, canonical)` upsert `om_alias_type`。
   - `defineRelationAlias(runner, alias, canonical)` upsert `om_alias_rel`。
   - `defineAttributeAlias(runner, typeName, aliasAttr, canonicalAttr)` upsert `om_alias_attr`。
   - 每次成功写入后调用 `invalidateAliasCache(runner)`，避免同一个 runner 在 alias 写入前已解析过名称时读到缓存旧值。
   - 名称只做现有风格的 trim/nonempty 检查。定义 API 不检查 canonical 是否存在，也不拒绝环；resolver 保持现有 cycle error。

2. Entity lifecycle deletion
   - `deleteEntity(runner, entityId)` 采用 `_withWriteTxIfPossible`。
   - 在同一 transaction 内依次删除：`om_property` 中 `entity_id` 匹配的所有 valid-time row，`om_edge` 中 `from_id` 或 `to_id` 匹配的所有 valid-time row，及 `om_entity` 中该 ID 的 row。
   - 每个删除都使用 relation-level physical remove，而非 current-time retraction；不存在目标行自然为无操作。

## 影响范围与修改点 (Impact)

- `cozo-lib-bun/cozo-om.js`：新增四个公开函数和 exports。
- `cozo-lib-bun/__tests__/om-alias-resolution.test.js`：将主要 authoring setup 转为 public APIs，并加入 upsert/cache/cycle coverage。
- `cozo-lib-bun/__tests__/om-entity-delete.test.js`：新增 temporal cascading and idempotency coverage。

## 决策摘要

- 详见 `decisions.md`。
- API 语义跟随 C#：alias 为 metadata upsert，cycle 在 resolve 时暴露；entity deletion 为 physical, cascading, idempotent operation。

## 风险 / 权衡

- Alias target 不存在可被写入：这保留迁移编排的灵活性，代价是错误在 resolve/use 时出现；与 C# 当前行为一致。
- 删除所有历史事实不可回滚：这是 C# 对等的显式物理删除语义；调用方若需要保留历史，应使用时态 retraction API 而非 deleteEntity。

## 兼容性设计

- 不修改任何 stored relation schema；旧 migration、snapshot、direct DSL authors 和 alias resolution 继续工作。
- 新 API 是 additive，`module.exports` 的现有成员不变。
