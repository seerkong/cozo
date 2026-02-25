## ADDED Requirements

### Requirement: Temporal 属性类型支持
系统 SHALL 在 `OmValueType` 中新增 `'Validity'` 类型，用于声明具有时间语义的属性。`defineAttribute` SHALL 支持 `value_type: 'Validity'`。`om_attr_def` 关系 SHALL 能存储该类型。

#### Scenario: 定义 Validity 类型属性
- **GIVEN** 已定义类型 `Employee`
- **WHEN** 调用 `defineAttribute(runner, 'Employee', 'valid_from', 'Validity', true)`
- **THEN** 属性定义成功，`om_attr_def` 中记录 `value_type = 'Validity'`

#### Scenario: 设置 Validity 类型属性值
- **GIVEN** `Employee` 有 `valid_from: Validity` 属性
- **WHEN** 调用 `setProperty(runner, entityId, 'valid_from', '2026-01-01T00:00:00Z')`
- **THEN** 属性值被写入，CozoDB 将其存储为 Validity 类型

### Requirement: Bi-temporal 属性历史（om_property 改造）
系统 SHALL 将 `om_property` 关系改造为 bi-temporal 结构：`om_property { entity_id, attr_name, valid_time: Validity => value, tx_time: String }`。`valid_time` 为业务生效时间（使用 CozoDB Validity 作为最后一个 key 列），`tx_time` 为系统记录时间（ISO-8601 字符串）。每次属性写入 SHALL 产生一条新的时间版本记录。

#### Scenario: 写入属性自动记录时间版本
- **GIVEN** 已定义类型 `Employee` 及属性 `department`(String)
- **AND** 实体 `emp1` 的 `department` 当前值为 `'Engineering'`
- **WHEN** 调用 `setProperty(runner, 'emp1', 'department', 'Product', { validTime: '2026-06-01T00:00:00Z' })`
- **THEN** `om_property` 中新增一条记录：`entity_id='emp1', attr_name='department', valid_time='2026-06-01', value='Product', tx_time=<当前系统时间>`
- **AND** 之前的 `'Engineering'` 记录仍然保留

#### Scenario: 不指定 validTime 时使用当前时间
- **GIVEN** 已定义类型 `Employee` 及属性 `name`(String)
- **WHEN** 调用 `setProperty(runner, 'emp1', 'name', 'Alice')` 不传 validTime
- **THEN** 系统使用当前时间作为 `valid_time` 和 `tx_time`

#### Scenario: 读取属性默认返回最新有效值
- **GIVEN** `emp1` 的 `department` 有两条历史记录：`Engineering@2026-01` 和 `Product@2026-06`
- **WHEN** 调用 `getProperty(runner, 'emp1', 'department')`（不指定时间）
- **THEN** 返回最新有效值 `'Product'`

### Requirement: 属性历史查询
系统 SHALL 提供 `getPropertyHistory(runner, entityId, attrName, options?)` API，返回指定属性的所有时间版本记录，按 `valid_time` 排序。`options` 可选参数包括 `{ from?: string, to?: string }` 用于时间范围过滤。

#### Scenario: 查询属性完整历史
- **GIVEN** `emp1` 的 `department` 有三条历史记录
- **WHEN** 调用 `getPropertyHistory(runner, 'emp1', 'department')`
- **THEN** 返回数组，每条包含 `{ value, valid_time, tx_time }`，按 valid_time 升序排列

#### Scenario: 按时间范围过滤历史
- **GIVEN** `emp1` 的 `department` 有 2025-01、2025-06、2026-01 三条记录
- **WHEN** 调用 `getPropertyHistory(runner, 'emp1', 'department', { from: '2025-05', to: '2025-12' })`
- **THEN** 仅返回 2025-06 的记录

### Requirement: 时间点快照查询（As-Of Query）
系统 SHALL 提供 `getPropertyAsOf(runner, entityId, attrName, timestamp)` API，利用 CozoDB 的 `@ timestamp` 语法返回指定时间点的属性值。系统 SHALL 提供 `getEntityViewAsOf(runner, entityId, timestamp)` API，返回实体在指定时间点的完整快照（所有属性的当时值）。

#### Scenario: 查询历史时间点的属性值
- **GIVEN** `emp1` 的 `department` 在 2025-01 为 `Engineering`，2026-01 变更为 `Product`
- **WHEN** 调用 `getPropertyAsOf(runner, 'emp1', 'department', '2025-06-01T00:00:00Z')`
- **THEN** 返回 `'Engineering'`（2025-06 时的有效值）

#### Scenario: 查询实体完整快照
- **GIVEN** `emp1` 在 2025-06 时有 `name='Alice'`, `department='Engineering'`, `role='IC'`
- **WHEN** 调用 `getEntityViewAsOf(runner, 'emp1', '2025-06-01T00:00:00Z')`
- **THEN** 返回包含所有属性在该时间点有效值的完整视图

#### Scenario: 查询未来时间点返回最新已知值
- **GIVEN** `emp1` 最新记录为 2026-01
- **WHEN** 调用 `getPropertyAsOf(runner, 'emp1', 'department', '2030-01-01T00:00:00Z')`
- **THEN** 返回 2026-01 的值（CozoDB Validity 的 as-of 语义）

### Requirement: Temporal 关系（边的有效期）
系统 SHALL 将 `om_edge` 关系改造为支持有效期：`om_edge { from_id, rel_name, to_id, valid_time: Validity => props, tx_time: String }`。`linkEntities` SHALL 接受可选的 `validTime` 参数。系统 SHALL 提供 `getNeighborsAsOf(runner, entityId, relName, timestamp)` 查询指定时间点的关系。

#### Scenario: 创建带有效期的关系
- **GIVEN** 已定义关系 `belongs_to`（Employee → Department）
- **WHEN** 调用 `linkEntities(runner, 'emp1', 'belongs_to', 'dept-eng', { validTime: '2025-01-01T00:00:00Z' })`
- **THEN** `om_edge` 中记录该关系及其 valid_time

#### Scenario: 查询某时间点的关系
- **GIVEN** `emp1` 在 2025-01 属于 `dept-eng`，2026-01 转到 `dept-product`
- **WHEN** 调用 `getNeighborsAsOf(runner, 'emp1', 'belongs_to', '2025-06-01T00:00:00Z')`
- **THEN** 返回 `dept-eng`

#### Scenario: 不指定 validTime 时使用当前时间
- **GIVEN** 已定义关系 `belongs_to`
- **WHEN** 调用 `linkEntities(runner, 'emp1', 'belongs_to', 'dept-eng')` 不传 validTime
- **THEN** 系统使用当前时间作为 valid_time

### Requirement: 关系历史查询
系统 SHALL 提供 `getEdgeHistory(runner, fromId, relName, toId?, options?)` API，返回指定关系的所有时间版本。当 `toId` 省略时返回该实体在该关系上的所有历史邻居。

#### Scenario: 查询关系完整历史
- **GIVEN** `emp1` 的 `belongs_to` 关系有多条历史记录（不同部门）
- **WHEN** 调用 `getEdgeHistory(runner, 'emp1', 'belongs_to')`
- **THEN** 返回所有历史关系记录，按 valid_time 排序

#### Scenario: 查询特定关系对的历史
- **GIVEN** `emp1` 与 `dept-eng` 的 `belongs_to` 关系有多次变更
- **WHEN** 调用 `getEdgeHistory(runner, 'emp1', 'belongs_to', 'dept-eng')`
- **THEN** 仅返回 emp1→dept-eng 的历史记录

### Requirement: Temporal 与行为层集成
系统 SHALL 确保 Action/Mutation/Constraint 与 temporal 属性协同工作。ActionContext 中的 `getProperty` 和 `setProperty` SHALL 支持 temporal 参数。约束校验 SHALL 基于当前有效值（latest）执行。

#### Scenario: Action 中使用 temporal setProperty
- **GIVEN** Action `transfer` 的 handler 调用 `ctx.setProperty('department', 'Product', { validTime: futureDate })`
- **WHEN** 执行该 Action
- **THEN** 属性以指定的 validTime 写入 om_property

#### Scenario: 约束基于最新有效值校验
- **GIVEN** 条件约束：当 `status='active'` 时 `end_date` 必须为空
- **AND** 实体最新有效 `status` 为 `'active'`
- **WHEN** 调用 `setProperty(runner, entityId, 'end_date', '2026-12-31')`
- **THEN** 约束校验基于最新有效的 `status` 值触发，抛出违反错误

### Requirement: Schema 迁移兼容
系统 SHALL 在 `initSchema` 中自动检测并迁移旧版 `om_property`（无 valid_time/tx_time）和 `om_edge`（无 valid_time/tx_time）到新的 bi-temporal 结构。迁移时旧数据的 valid_time 和 tx_time SHALL 使用迁移执行时的当前时间。

#### Scenario: 旧 schema 自动迁移
- **GIVEN** 数据库中存在旧版 `om_property { entity_id, attr_name => value }`
- **WHEN** 调用 `initSchema(runner)` 使用新版代码
- **THEN** 系统检测到旧 schema，将数据迁移到新结构，旧数据获得当前时间作为 valid_time 和 tx_time
- **AND** 迁移后所有现有 API 正常工作

#### Scenario: 新 schema 无需迁移
- **GIVEN** 数据库中已是新版 bi-temporal schema
- **WHEN** 调用 `initSchema(runner)`
- **THEN** 无迁移操作，正常初始化

### Requirement: 重构 approval-flow Demo 使用 Temporal API
系统 SHALL 将 approval-flow demo 中手写的 raw CozoDB 时间旅行代码替换为 om 层 temporal API。demo 中的 `approval_request_ledger` 关系、`:put` 写入、`@ $as_of` 查询 SHALL 全部改为使用 `setProperty`/`getPropertyAsOf`/`getPropertyHistory` 等 om API。

#### Scenario: approval-flow 使用 om temporal API
- **GIVEN** 用户加载 approval-flow demo
- **WHEN** 执行 submit → approve 流程
- **THEN** 时间轴数据通过 om temporal API 写入和查询，不再使用 raw CozoDB 脚本
- **AND** 时间轴展示结果与重构前一致

### Requirement: 新增组织架构 Temporal Demo
系统 SHALL 新增一个独立的组织架构变迁 demo（org-timeline），展示：部门调整、人员调动的时间轴，以及任意时间点的组织架构快照查询。

#### Scenario: 加载组织架构 temporal demo
- **GIVEN** 用户访问 viz 的 demo 列表
- **WHEN** 选择 org-timeline demo
- **THEN** 工作簿加载包含 Department/Employee/Team 类型定义、人员调动历史数据
- **AND** 可执行时间点快照查询，查看任意历史时刻的组织架构

#### Scenario: 组织架构时间轴查询
- **GIVEN** demo 中 Alice 在 2025-01 属于 Engineering，2025-07 调到 Product，2026-01 调到 Management
- **WHEN** 执行 "2025-06 组织架构快照" 查询
- **THEN** 结果显示 Alice 属于 Engineering
- **AND** 执行 "2026-02 组织架构快照" 查询时显示 Alice 属于 Management
