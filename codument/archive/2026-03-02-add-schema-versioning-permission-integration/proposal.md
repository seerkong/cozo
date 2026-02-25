# 变更：Schema 版本化 + 本体权限集成（Phase 4）

## 背景和动机 (Context And Why)

当前 cozo-om 已具备：类型继承、多态查询、行为层（Action/Constraint/Computed）、以及 temporal（bi-temporal 属性/关系与 as-of 查询）。但生产化演进还缺少两个关键能力：

1) **Schema 版本化与迁移（Schema Evolution）**：schema 变更需要可追踪、可审计、可回滚，并能在重命名/兼容变更下保持调用方稳定。

2) **权限与本体集成（Ontology-level Security）**：现有 viz 的权限 demo（RBAC/ABAC/Timeline）是独立模型演示，尚未与本体实体/属性/关系的访问控制联动。

本次迭代实现演进规划中的 Phase 4：在不破坏现有 demo 的前提下，补齐 schema 版本化 + 本体权限集成，并在 viz 中新增两个新的演示入口。

## “要做”和“不做” (Goals / Non-Goals)

**目标:**
- 提供 schema 版本元数据、迁移应用、历史审计、回滚/降级的能力（最小实现以 alias/兼容解析为主，不要求多套 version-namespaced schema 关系并存）。
- 支持的演进类型：新增属性/关系、重命名属性/关系、修改 valueType/required、修改继承（parent_type/mixins）、回滚/降级。
- 在 cozo-om 中引入**可选**的权限集成能力（默认不启用，不影响既有调用方）。
- 权限演示采用 **Hybrid ABAC + Relation-Path**：路径推导用于限定可见资源范围，ABAC 用于细化 allow/deny 与字段级可见性。
- Viz 增加两个新的 top-nav tab/page：
  - 本体论+权限（新入口，不改旧 `/permission` demo）
  - schema 版本化（新入口）

**非目标:**
- 不修改现有 `/permission` 页面和其内置模型/交互（保持旧权限功能 demo 不动）。
- 不要求对实例数据做 destructive rewrite 作为重命名迁移的前置条件（默认通过 alias 解析实现兼容；数据重写仅作为可选优化/维护工具）。
- 不实现无限递归的路径推导（只支持固定长度路径与有限 fanout 的推导与解释）。
- 不引入新的外部服务或独立存储；所有元数据存储在 CozoDB 关系中。

## 变更内容（What Changes）

- cozo-om 增加 schema 版本化元数据关系与管理 API：
  - 当前版本状态查询（current_version/checksum）
  - 应用迁移（apply migration）与迁移历史（migration log）
  - schema snapshot 与版本 diff
  - 回滚/降级（strict vs force）
- cozo-om 增加 alias/名称解析机制：
  - 类型/关系/属性的 alias 映射
  - 读写 API 接受旧名称并规范化为 canonical；对外输出 canonical
- cozo-om 增加权限集成（可选启用）：
  - `checkAccess`（Hybrid ABAC + relation-path）与可解释输出（matched policies + witness path + ABAC 条件对比）
  - 权限策略引用与 alias 解析联动，保证重命名后策略可用
- viz server 新增 API（与现有 `/api/permission/*` 分离）：
  - `/api/schema/*`：版本/迁移/diff/回滚
  - `/api/governance/*`：本体权限演示运行与解释
- viz frontend 新增两个页面并加入顶栏导航（保留 `/ontology` 与 `/permission`）：
  - `/governance`：本体论+权限
  - `/schema`：schema 版本化
- 增量 E2E：新增对两个新页面的 Playwright 用例，保证旧 `/permission` 用例不受影响。

## 影响范围（Impact）

- 受影响的能力规范：cozo-om（新增 schema versioning + permission integration）、cozo-lib-bun-viz（新增页面/端点）
- 受影响的代码模块：
  - `cozo-lib-bun/cozo-om.js`（新增 schema versioning + alias resolution + permission hooks/API）
  - `cozo-lib-bun/__tests__/`（新增单元测试）
  - `cozo-lib-bun-viz/server/src/index.js`（新增端点，避免与现有 permission demo 耦合）
  - `cozo-lib-bun-viz/frontend/src/App.vue` / `cozo-lib-bun-viz/frontend/src/router.ts`（新增 top-nav 与路由）
  - `cozo-lib-bun-viz/frontend/src/pages/*`（新增页面）
  - `cozo-lib-bun-viz/e2e/*`（新增 E2E 用例）
