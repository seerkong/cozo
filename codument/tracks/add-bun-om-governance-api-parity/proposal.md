# 变更：补齐 Bun OM 治理 API 对等能力

## 背景和动机 (Context And Why)

`cozo-lib-bun` 已经持久化 alias metadata 并提供解析、迁移和 schema 生命周期能力，但调用方只能依赖 migration 内部逻辑或直接写 `om_alias_*` relation 才能创建 alias。它也缺少对象级删除 API，导致应用必须自行删除实体、时态属性和入/出边，容易留下孤儿事实。

`.NET` OM 已公开提供三种 alias authoring API 和物理级联的 `DeleteEntityAsync`。本 track 将 Bun 补到相同的底层对象系统能力，不改变现有 schema relation 或迁移格式。

## "要做"和"不做" (Goals / Non-Goals)

**目标:**
- 在 Bun 导出 `defineTypeAlias`、`defineRelationAlias`、`defineAttributeAlias`。
- 在 Bun 导出 `deleteEntity`，以单个写事务物理级联删除实体、所有时态属性和所有接触该实体的时态边。
- 覆盖公开 API、cache invalidation、upsert、cycle resolution、时态行清理、幂等删除及无关数据保留的测试。

**非目标:**
- 不改变 `om_alias_*` relation 的结构、schema migration 语法或 snapshot 格式。
- 不新增 alias target existence 校验或定义时 cycle rejection；这会超出 C# 参考实现的语义。
- 不实现 Bun 已有但 C# 未覆盖的查询/行为层功能。

## 变更内容 (What Changes)

- 在 `cozo-om.js` 的 alias-resolution 区域新增三个公开定义函数：验证非空名称、写入现有 relation、清除对应 runner alias cache。
- 新增 `deleteEntity`：复用 `_withWriteTxIfPossible`，按实体 ID 删除 `om_property` 全部历史、`om_edge` 中任一端点匹配的全部历史，最后删除 `om_entity`。
- 增加 focused Bun tests，直接查询存储 relation 验证时间维度上的完整清理。

## 影响范围 (Impact)

- 受影响模块：`cozo-lib-bun/cozo-om.js` 与 `cozo-lib-bun/__tests__/`。
- 对现有用户兼容：纯新增 API；既有 alias 直接写 relation 和既有 migration 均保持可用。
- 风险：删除语义不可逆，但仅作用于调用方指定 entity ID，并要求所有写操作处于同一事务。

## 验收

- Bun 的 alias 定义 API 经公开导出并能立即影响 resolver 与实体/属性/关系写入。
- 删除 API 清理全部历史 property/edge row，且不会删除无关实体或边。
- 目标 Bun test files 与完整 Bun test suite 通过。
