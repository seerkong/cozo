## ADDED Requirements

### Requirement: Schema 版本化元数据
系统 SHALL 支持为本体 schema 维护版本状态与历史记录。版本化元数据 SHALL 与现有 `om_type`/`om_attr_def`/`om_rel_def` 等 schema 关系并存，且不要求将版本号加入现有 schema 关系的 key 列（避免破坏性变更）。

#### Scenario: 初始化 schema 时创建版本化元数据
- **GIVEN** 一个新的空数据库实例
- **WHEN** 调用 `initSchema(runner)`
- **THEN** 系统创建版本化元数据关系（如 schema state / version / migration / snapshot / alias 映射）
- **AND** 设定 `current_version = 1`

#### Scenario: 查询当前 schema 状态
- **GIVEN** 数据库已初始化 schema
- **WHEN** 调用 `getSchemaState(runner)`
- **THEN** 返回 `{ currentVersion, checksum }`

### Requirement: Schema 迁移（Migration）记录与应用
系统 SHALL 支持以结构化迁移规范（migration spec）应用 schema 变更，并记录迁移历史。迁移记录 SHALL 可用于审计与回滚。

#### Scenario: 应用迁移并记录历史
- **GIVEN** 当前 `current_version = 1`
- **WHEN** 调用 `applySchemaMigration(runner, spec)` 将 schema 升级到版本 2
- **THEN** 系统更新 `current_version = 2`
- **AND** 系统写入一条迁移记录（from/to/applied_at/status/summary）
- **AND** 系统为目标版本写入 schema snapshot

#### Scenario: 多版本共存通过别名解析实现
- **GIVEN** schema 从 v1 演进到 v2，存在重命名
- **WHEN** 使用旧名称与新名称调用 API（读/写）
- **THEN** 系统通过别名映射解析到 canonical 名称
- **AND** API 对外返回 canonical 名称

### Requirement: 重命名兼容（Alias）
系统 SHALL 支持类型/关系/属性的重命名，并保证旧名称在升级后仍可用于读写（通过别名解析）。

#### Scenario: 属性重命名后旧名称仍可读取
- **GIVEN** v1 中属性名为 `Employee.department`
- **AND** v2 将该属性重命名为 `Employee.org_unit`
- **WHEN** 调用 `getProperty(runner, empId, 'department')`
- **THEN** 返回与 `getProperty(runner, empId, 'org_unit')` 一致的结果

#### Scenario: 旧名称写入被规范化为 canonical
- **GIVEN** v2 中 `Employee.department` 是 `Employee.org_unit` 的 alias
- **WHEN** 调用 `setProperty(runner, empId, 'department', 'X')`
- **THEN** 系统写入 canonical 属性 `org_unit`

#### Scenario: canonical 与 alias 同时存在时的确定性优先级
- **GIVEN** 同一语义字段同时存在 canonical 与 alias 的值
- **WHEN** 调用 `getProperty`（或 `getPropertyAsOf`）
- **THEN** 系统返回 canonical 值
- **AND** 系统可提供诊断信息提示存在重复

### Requirement: Schema 回滚/降级（Rollback/Downgrade）
系统 SHALL 支持将 schema 回滚到历史版本。回滚 SHALL 恢复 schema 定义与别名映射；回滚不要求对实例数据做 destructive delete。

#### Scenario: 严格回滚在不兼容时阻止
- **GIVEN** v3 对 `required`/`valueType` 做了收紧
- **WHEN** 调用 `rollbackSchema(runner, targetVersion, { strict: true })`
- **AND** 回滚会导致现有实例数据不满足目标版本约束
- **THEN** 回滚失败并返回包含 entityId/field 的诊断

#### Scenario: 强制回滚允许但返回诊断
- **GIVEN** 同上
- **WHEN** 调用 `rollbackSchema(runner, targetVersion, { strict: false })`
- **THEN** 回滚成功
- **AND** 返回诊断摘要供后续修复

### Requirement: valueType/required/继承变更的预检与迁移
系统 SHALL 支持以下 schema 演进类型，并在可能破坏兼容性时提供 preflight 校验：
- 新增属性/关系
- 重命名属性/关系
- 修改 `valueType` / `required`
- 修改类型继承（parent_type / mixins）

#### Scenario: valueType 变更必须通过预检或显式转换
- **GIVEN** 某属性从 String 变更为 Number
- **WHEN** 应用迁移（strict precheck）
- **THEN** 若存在不可转换的旧值，迁移失败并返回诊断

#### Scenario: required 收紧支持 backfill 后收紧
- **GIVEN** v1 属性 `Employee.level` 为非必填
- **WHEN** 迁移步骤包含“backfill 默认值”并随后将 required 设为 true
- **THEN** 迁移成功且后续写入/校验遵循新的 required 规则

#### Scenario: 继承变更必须避免循环与冲突
- **GIVEN** schema 修改 parent_type 或 mixins
- **WHEN** 应用迁移
- **THEN** 系统检测并阻止继承循环
- **AND** 系统检测并阻止继承属性 valueType/required 冲突

### Requirement: 本体论 + 权限集成（Hybrid ABAC + Relation-Path）
系统 SHALL 新增可选的权限能力，用于对本体实体/属性/关系执行访问控制。

权限模型 SHALL 同时支持：
- **Relation-path 推导**：用本体关系路径限定可见实体范围（scope）
- **ABAC 细化**：在 scope 内按 subject/resource attributes 与策略规则决定 allow/deny，并支持字段级隐藏

#### Scenario: checkAccess 返回 allow/deny 与解释
- **GIVEN** 存在 subject（用户）与 resource（本体实体）
- **WHEN** 调用 `checkAccess(runner, { subjectId, action, resourceId })`
- **THEN** 返回 `{ allow, matchedPolicies, explanation }`
- **AND** explanation 包含：命中的 path witness（若适用）与 ABAC 条件对比

#### Scenario: 权限策略对 schema 重命名保持稳定
- **GIVEN** 策略引用了属性/关系名
- **WHEN** schema 迁移发生重命名
- **THEN** 策略引用通过 alias 解析仍可工作

#### Scenario: 权限能力默认不影响既有调用方
- **GIVEN** 未显式启用 policy
- **WHEN** 现有 API（如 `getEntityView`/`findByType`/`runQuery`）被调用
- **THEN** 行为与启用权限前一致

### Requirement: Viz 新增演示 Tab（不改旧权限 demo）
系统 SHALL 在 viz 前端新增两个新的 top-nav tab/page（不修改现有 `/permission` demo 的业务逻辑与交互）：
- **本体论 + 权限**：展示 Hybrid ABAC + Relation-path 对本体数据的访问控制与解释
- **Schema 版本化**：展示 schema 版本列表、diff、应用迁移、回滚的演示

#### Scenario: 旧权限 demo 不受影响
- **GIVEN** 用户访问 `/permission`
- **WHEN** 选择 RBAC/ABAC/Timeline 权限模型并执行查询
- **THEN** 行为与本 track 前一致

#### Scenario: 新增本体论+权限页面可运行并展示解释
- **GIVEN** 用户进入新页面
- **WHEN** 选择 subject/action/resource 并执行检查
- **THEN** 页面展示 allow/deny 与解释信息

#### Scenario: 新增 schema 版本化页面可运行迁移/回滚演示
- **GIVEN** 用户进入新页面
- **WHEN** 应用一次 schema 迁移并查看 diff
- **AND** 触发一次回滚
- **THEN** 页面展示版本切换与状态变化

### Requirement: Governance 页面 Tabs 与数据准备（可编辑）
系统 SHALL 在 `/governance` 页面内提供清晰的子 Tabs，将“数据准备（seed/初始化）”与“权限查询（checkAccess/explain）”分离，并允许用户在页面中查看与编辑演示的初始数据，再以编辑后的数据重新初始化演示环境。

#### Scenario: /governance 页面包含两个子 tab
- **GIVEN** 用户进入 `/governance`
- **THEN** 页面包含至少两个子 tab：`数据准备` 与 `权限查询`
- **AND** `权限查询` tab 中可执行权限检查并展示 allow/deny 与解释
- **AND** 切换 tab 不应丢失已输入的 subjectId/resourceId/action（除非用户显式重置）

#### Scenario: 数据准备 tab 显示默认 seed 数据并允许编辑
- **GIVEN** 用户进入 `/governance` 的 `数据准备` tab
- **THEN** 页面展示一份默认的 seed 数据模板
- **AND** 该模板以可视化表格/工作表（类似“对象建模分析”页面顶部表格）展示，使用户可直接查看初始数据
- **AND** 用户可修改该数据（实体/属性/边/策略等）

#### Scenario: 编辑后的数据可用于重新初始化演示环境
- **GIVEN** 用户在 `数据准备` tab 中对 seed 数据进行了修改
- **WHEN** 用户点击“重新初始化/应用”
- **THEN** 后端使用更新后的 seed 数据重新初始化演示数据与策略
- **AND** 后续在 `权限查询` tab 运行时使用新数据

#### Scenario: 兼容旧 seed 调用
- **GIVEN** 现有调用方以旧方式调用 `POST /api/governance/seed`（无请求体）
- **WHEN** 触发 seed
- **THEN** 系统仍使用默认 seed 数据初始化演示环境

### Requirement: 自动化测试与门控
系统 SHALL 为新增能力提供单元测试与端到端测试，并确保以下命令通过：
- `bun test`
- `bun test --coverage`
- `codument validate --strict`
- `npm run test:e2e`（在 `cozo-lib-bun-viz/`）
