## 上下文

本 track 实现“本体论支持演进规划”中的 Phase 4：

1) **Schema 版本化与迁移（Schema Evolution）**：为 cozo-om 的 schema（类型/属性/关系/继承）提供版本状态、迁移记录、快照、diff、回滚/降级与兼容策略。

2) **权限与本体集成（Ontology-level Security）**：在 cozo-om 中提供可选的权限检查与解释能力，并在 viz 中新增一个独立页面进行演示。

约束：
- **旧的权限功能 demo 不要动**：`/permission` 页面和其 RBAC/ABAC/Timeline 模型维持现状。
- viz 只新增两个新的 top-nav 页面：
  - 本体论 + 权限（Hybrid ABAC + Relation-Path）
  - schema 版本化
- 尽量避免破坏性变更：不要求把 `version` 加入现有 `om_*` schema 关系的 key 列。

## 方案概览

### 1) Schema 版本化：元数据 + 快照 + alias 兼容（而非 version-namespaced schema 关系）

核心立场：保持现有 `om_type`/`om_attr_def`/`om_rel_def` 作为“当前激活版本”的 schema 真相；通过独立元数据关系记录版本/迁移历史，并通过 alias/名称解析实现多版本兼容。

#### 1.1 元数据关系（initSchema 创建，幂等）

- `om_schema_state { id => current_version, current_checksum }`
  - 仅一行：`id='default'`
- `om_schema_version { version => created_at, label, description, parent_version, checksum }`
- `om_schema_migration { migration_id => from_version, to_version, applied_at, applied_by, status, error, summary_json }`
- `om_schema_snapshot { version => snapshot_json }`

#### 1.2 Alias 映射（用于 rename 兼容与“多版本共存”语义）

- `om_alias_type { alias => canonical }`
- `om_alias_rel { alias => canonical }`
- `om_alias_attr { type_name, alias_attr => canonical_attr }`

兼容语义：
- 写入路径：所有输入名称先 canonicalize（alias -> canonical）。
- 读取路径：canonical 优先；若 canonical 无值且存在 alias 候选，可按 deterministic 规则 fallback。
- 冲突诊断：若 canonical 与 alias 同时存在值，canonical 胜出，并输出诊断。

#### 1.3 MigrationSpec（结构化迁移输入）

最小可扩展结构：
- `migration_id`, `from_version`, `to_version`, `label`, `steps[]`
- step types（本期覆盖范围）：
  - add attribute/relation
  - rename attribute/relation
  - change valueType/required
  - change parent_type/mixins
  - rollback/downgrade

对不兼容变更（valueType/required/hierarchy）要求：
- 必须有 preflight 检查（strict），或显式提供 backfill/transform 步骤。

#### 1.4 Rollback/Downgrade（schema 回滚 != data 回滚）

回滚策略：恢复“目标版本的 schema 定义 + alias 映射 + 权限策略元数据”，并更新 `current_version`。

实例数据不做 destructive 删除；通过 alias 兼容保证旧调用在回滚后可用。

回滚模式：
- `strict: true`：若回滚会导致现有实例数据违反目标版本约束，则阻止并返回诊断。
- `strict: false`：允许回滚但返回诊断摘要。

### 2) 权限集成：Hybrid ABAC + Relation-Path（可解释）

目标：提供一个独立于旧 `/permission` demo 的“本体论+权限”演示入口，同时在 cozo-om 层提供可选 API。

#### 2.1 权限数据模型（存储在 Cozo 关系中）

最小集合（可在实现时微调）：
- `om_perm_action { action => description }`
- `om_perm_policy { policy_id => effect, action, resource_type, enabled, description }`
- `om_perm_abac_rule { policy_id, left_ref, op, right_ref => }`
- `om_perm_path_rule { policy_id, path => }`

其中：
- relation-path rule 用于限定 scope（可见资源集合），路径为固定长度（避免无限递归）。
- ABAC rule 在 scope 内做 allow/deny 与字段级隐藏。

#### 2.2 API：checkAccess（解释友好）

- `checkAccess(runner, { subjectId, action, resourceId, asOf? }) -> { allow, matchedPolicies, explanation }`
- explanation 包含：
  - 命中的 policy
  - relation-path witness（若适用）
  - ABAC 条件对比（left value / op / right value）

#### 2.3 与 schema versioning 的耦合点

权限策略引用（属性名/关系名/类型名）需要与 alias 解析联动：
- schema rename 后策略仍可解析到 canonical。
- rollback 后策略引用按目标版本的 alias map 继续工作。

### 3) Viz：新增两个 top-nav 页面（不改旧权限 demo）

#### 3.1 前端路由

- 保留：`/ontology`（现有对象建模分析），`/permission`（旧权限 demo）
- 新增：
  - `/governance`：本体论 + 权限
  - `/schema`：schema 版本化

#### 3.2 后端端点

- 不触碰现有：`/api/permission/models`、`/api/permission/run`
- 新增：
  - `/api/schema/*`：list versions / diff / apply migration / rollback
  - `/api/governance/*`：check access / explain / demo seed

## 影响范围与修改点（Impact）

- `cozo-lib-bun/cozo-om.js`：
  - initSchema 增加 schema versioning / alias / perm 相关关系初始化
  - 增加 schema 管理 API（getSchemaState/listVersions/applyMigration/rollback/diff）
  - 增加 alias resolution（不破坏既有 API；默认启用兼容解析）
  - 增加权限 API（默认不启用强制过滤；由 viz 演示显式调用）

- `cozo-lib-bun-viz/server/src/index.js`：
  - 新增 `/api/schema/*` 与 `/api/governance/*`，不改 `/api/permission/*`

- `cozo-lib-bun-viz/frontend/src/App.vue` / `cozo-lib-bun-viz/frontend/src/router.ts`：
  - 顶栏新增两个入口 + 新路由

- `cozo-lib-bun-viz/frontend/src/pages/*`：
  - 新增两个页面（最小独立数据集）

## 决策

- 决策：多版本共存以 alias/兼容解析为主，而非 version-namespaced schema 关系。
  - 理由：避免对所有既有 API 与 query 进行破坏性升级；rollback 更安全。

- 决策：rename migration 默认不做实例数据 bulk rewrite。
  - 理由：降低迁移风险，支持回滚；通过 alias fallback 保持读写兼容。

- 决策：权限集成提供可选 API（默认不影响调用方）。
  - 理由：保持库的通用性与向后兼容；演示入口显式启用。

## 风险 / 权衡

- alias 歧义/循环：必须在写入 alias map 时检测并拒绝。
- canonical 与 alias 并存：需定义确定性优先级（canonical 优先）并提供诊断。
- required/valueType 收紧：需要 preflight + backfill/transform，否则会导致 ingest/validate 失败。
- rollback 仅回滚 schema：可能使现有实例数据在目标版本下不合法；提供 strict/force 模式。
- relation-path 权限推导成本：限制路径长度与 fanout，并提供解释输出的上限。

## 待解决问题

- schema 迁移 spec 的具体序列化格式（JSON vs 表格 vs 关系存储）在实现前需要最终敲定。
- 权限模型中“字段级隐藏”的表达方式：是返回 filtered view，还是返回 mask map。
