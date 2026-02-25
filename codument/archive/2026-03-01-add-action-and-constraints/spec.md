## ADDED Requirements

### Requirement: Action 定义与注册
系统 SHALL 支持为 Object Type 注册 Action（业务级可执行操作）。Action 由名称、参数 schema、以及一个将 Action 转换为一组 Mutation 的处理函数组成。Action 注册通过 `defineAction(runner, typeName, actionName, handler)` 完成。子类型 SHALL 继承父类型的 Action；子类型 SHALL 可以通过重新调用 `defineAction` 覆盖继承的 Action，并可在 handler 中通过 `callParentAction(ctx, actionName, params)` 调用父类型的原始 handler 以实现扩展（类似 super 调用）。执行时按最近祖先优先（MRO）解析 Action 定义。

#### Scenario: 注册并执行 Action
- **GIVEN** 已定义类型 `PurchaseOrder` 及属性 `status`(String)
- **WHEN** 调用 `defineAction(runner, 'PurchaseOrder', 'approve', handler)` 注册 Action
- **AND** 调用 `executeAction(runner, entityId, 'approve', params)` 执行
- **THEN** handler 被调用并返回一组 Mutation，系统按顺序执行这些 Mutation

#### Scenario: 对不存在的 Action 执行报错
- **GIVEN** 类型 `PurchaseOrder` 未注册 `reject` Action
- **WHEN** 调用 `executeAction(runner, entityId, 'reject', {})`
- **THEN** 系统抛出错误，提示 Action 不存在

#### Scenario: Action 继承
- **GIVEN** 类型 `Asset` 注册了 Action `decommission`
- **AND** `ITAsset` 继承自 `Asset`
- **WHEN** 对 `ITAsset` 实例调用 `executeAction(runner, entityId, 'decommission', {})`
- **THEN** 执行成功，使用从 `Asset` 继承的 Action 定义

#### Scenario: 子类型覆盖并扩展父类型 Action
- **GIVEN** 类型 `Asset` 注册了 Action `decommission`，其 handler 返回 `[{mutation: 'setStatus', params: {status: 'decommissioned'}}]`
- **AND** `ITAsset` 继承自 `Asset`，且 `ITAsset` 有额外属性 `ip_address`
- **WHEN** 对 `ITAsset` 调用 `defineAction(runner, 'ITAsset', 'decommission', extendedHandler)` 覆盖该 Action
- **AND** `extendedHandler` 内部调用 `callParentAction(ctx, 'decommission', params)` 获取父类 Mutation 列表，再追加 `[{mutation: 'clearIp', params: {}}]`
- **THEN** 对 `ITAsset` 实例执行 `decommission` 时，先执行父类的 `setStatus` Mutation，再执行子类的 `clearIp` Mutation

#### Scenario: 未覆盖时使用最近祖先的 Action
- **GIVEN** `Server` 继承自 `ITAsset` 继承自 `Asset`
- **AND** `ITAsset` 覆盖了 `decommission` Action，`Server` 未覆盖
- **WHEN** 对 `Server` 实例执行 `decommission`
- **THEN** 使用 `ITAsset` 的覆盖版本（最近祖先优先）

### Requirement: Mutation 定义与原子执行
系统 SHALL 支持定义 Mutation（原子数据变更操作）。Mutation 由名称和执行函数组成，通过 `defineMutation(runner, typeName, mutationName, executor)` 注册。Action handler 返回的 Mutation 列表 SHALL 在单次事务语义下按顺序执行。

#### Scenario: 定义并通过 Action 触发 Mutation
- **GIVEN** 类型 `PurchaseOrder` 定义了 Mutation `setStatus` 和 `addAuditLog`
- **AND** Action `approve` 的 handler 返回 `[{mutation: 'setStatus', params: {status: 'approved'}}, {mutation: 'addAuditLog', params: {action: 'approve'}}]`
- **WHEN** 执行 `approve` Action
- **THEN** `setStatus` 和 `addAuditLog` 按顺序执行

#### Scenario: Mutation 执行失败时回滚
- **GIVEN** Action `approve` 触发两个 Mutation
- **WHEN** 第二个 Mutation 执行失败
- **THEN** 第一个 Mutation 的效果也被回滚，实体状态不变

### Requirement: Action 拦截器（Interceptor）
系统 SHALL 支持在 Action 执行前后注册拦截器。拦截器通过 `addInterceptor(runner, typeName, actionName, phase, handler)` 注册，`phase` 为 `'before'` 或 `'after'`。`before` 拦截器可阻止 Action 执行（抛出错误）；`after` 拦截器在 Mutation 全部完成后执行。

#### Scenario: before 拦截器阻止 Action
- **GIVEN** 类型 `PurchaseOrder` 的 `approve` Action 注册了 before 拦截器，校验 `status` 必须为 `pending`
- **AND** 某 PurchaseOrder 实例的 `status` 为 `approved`
- **WHEN** 调用 `executeAction(runner, entityId, 'approve', {})`
- **THEN** before 拦截器抛出错误，Action 不执行，实体状态不变

#### Scenario: after 拦截器执行后置逻辑
- **GIVEN** `approve` Action 注册了 after 拦截器（如发送通知）
- **WHEN** Action 及其 Mutation 全部成功执行
- **THEN** after 拦截器被调用

### Requirement: 条件约束（Conditional Constraint）
系统 SHALL 支持定义条件约束：当实体满足某条件时，强制要求另一组属性/关系条件成立。通过 `defineConstraint(runner, typeName, constraintName, { when, then, message })` 注册。

#### Scenario: 条件约束校验通过
- **GIVEN** 类型 `Employee` 定义了约束：当 `status='active'` 时 `end_date` 必须为空
- **AND** 某 Employee 实例 `status='active'`, `end_date` 未设置
- **WHEN** 调用 `validateConstraints(runner, entityId)`
- **THEN** 校验通过

#### Scenario: 条件约束校验失败
- **GIVEN** 同上约束定义
- **AND** 某 Employee 实例 `status='active'`, `end_date='2026-12-31'`
- **WHEN** 调用 `validateConstraints(runner, entityId)`
- **THEN** 返回约束违反错误，包含约束名称和 message

#### Scenario: 写入时自动触发条件约束
- **GIVEN** 同上约束定义
- **WHEN** 调用 `setProperty(runner, entityId, 'end_date', '2026-12-31')` 且当前 `status='active'`
- **THEN** 系统抛出约束违反错误，属性未被写入

#### Scenario: skipConstraints 跳过校验
- **GIVEN** 同上约束定义
- **WHEN** 调用 `setProperty(runner, entityId, 'end_date', '2026-12-31', { skipConstraints: true })`
- **THEN** 属性被写入，不触发约束校验

### Requirement: 跨实体约束（Cross-Entity Constraint）
系统 SHALL 支持定义跨实体约束：对某类型的所有实例或通过关系关联的实体集合施加聚合条件。通过 `defineConstraint(runner, typeName, constraintName, { scope: 'cross-entity', ... })` 注册。

#### Scenario: 唯一性约束
- **GIVEN** 类型 `Department` 定义了跨实体约束：通过 `heads` 关系关联的 Employee 最多 1 个
- **AND** 某 Department 已有 1 个 head
- **WHEN** 尝试 `linkEntities(runner, deptId, 'heads', newEmployeeId)`
- **THEN** 系统抛出约束违反错误

#### Scenario: 跨实体约束显式校验
- **GIVEN** 同上约束定义
- **WHEN** 调用 `validateConstraints(runner, deptId)`
- **THEN** 系统检查该 Department 的 heads 关系数量并返回校验结果

### Requirement: 派生规则（Derived/Computed Property）
系统 SHALL 支持定义派生属性：其值由一个计算函数根据实体自身属性和/或关联实体属性自动计算。通过 `defineComputed(runner, typeName, attrName, computeFn)` 注册。派生属性 SHALL 在读取时按需计算（lazy），不持久化存储。

#### Scenario: 定义并读取派生属性
- **GIVEN** 类型 `PurchaseOrder` 定义了派生属性 `total`，计算逻辑为关联 LineItem 的 `unit_price × quantity` 之和
- **WHEN** 调用 `getEntityView(runner, poId)` 或 `getProperty(runner, poId, 'total')`
- **THEN** 返回结果中包含 `total` 字段，值为实时计算结果

#### Scenario: 派生属性不可手动写入
- **GIVEN** `total` 是 `PurchaseOrder` 的派生属性
- **WHEN** 调用 `setProperty(runner, poId, 'total', 999)`
- **THEN** 系统抛出错误，提示不可写入派生属性

#### Scenario: 派生属性用于约束
- **GIVEN** 类型 `Asset` 定义了派生属性 `risk_score`
- **AND** 定义了约束：`risk_score > 80` 时 `requires_review` 必须为 `true`
- **WHEN** 约束校验时
- **THEN** 系统先计算 `risk_score` 再评估约束条件

### Requirement: 约束继承
系统 SHALL 支持子类型继承父类型定义的所有约束。子类型 SHALL 可以定义额外约束，但 SHALL NOT 移除或放宽继承的约束。

#### Scenario: 子类型继承父类型约束
- **GIVEN** `Asset` 定义了约束 `name_not_empty`
- **AND** `ITAsset` 继承自 `Asset`
- **WHEN** 对 `ITAsset` 实例调用 `validateConstraints`
- **THEN** `name_not_empty` 约束被校验

### Requirement: Schema 扩展兼容
系统 SHALL 在 `initSchema` 中新增 Action/Mutation/Constraint/Computed 所需的存储关系，且 SHALL 保持对现有无 Action/Constraint 的 schema 完全向后兼容。

#### Scenario: 现有代码无需修改
- **GIVEN** 现有代码不使用 Action/Constraint/Computed
- **WHEN** 升级到新版本后执行
- **THEN** 所有现有 API 行为不变

### Requirement: Action 与约束可视化 Demo
系统 SHALL 在 cozo-lib-bun-viz 中新增一个独立 demo，展示 Action/Mutation 执行流程、拦截器、条件约束、跨实体约束和派生属性的使用。

#### Scenario: 加载 Action 与约束 Demo
- **GIVEN** 用户访问 cozo-lib-bun-viz 的本体 demo 页面
- **WHEN** 选择 Action 与约束 demo
- **THEN** 工作簿加载包含 Action 定义、约束定义的类型结构和示例数据
- **AND** 可执行展示 Action 触发、约束校验、派生属性计算的查询

### Requirement: Demo 行为层定义表格化展示
系统 SHALL 在审批流 demo 的工作簿中，以独立 sheet 的形式展示 Action/Mutation/Interceptor/Constraint/Computed 的定义元数据，按类别分 sheet，便于查看规则与代码逻辑映射。

#### Scenario: 行为层定义按类别展示
- **GIVEN** 用户加载审批流 demo
- **WHEN** 用户查看工作簿中的定义 sheet
- **THEN** 用户可以分别在 Action/Mutation/Interceptor/Constraint/Computed sheet 中看到对应定义的名称、归属类型、描述和规则摘要

### Requirement: Demo 使用 CozoDB 时间旅行展示审批时间轴
系统 SHALL 在审批流 demo 中利用 CozoDB time travel 能力记录审批状态/决策的时间轴。submit 时 SHALL 携带生效时间字段 `effective_at`；当审批通过后录入系统时 SHALL 使用该 `effective_at` 写入 time travel relation，并在查询结果中以表格形式展示时间轴。

#### Scenario: 生效时间写入 time travel relation 并可回看
- **GIVEN** 用户对某审批请求执行 submit，并提供 future 的 `effective_at`
- **WHEN** 用户执行 approve 使该请求通过
- **THEN** 系统将 "approved" 决策按 `effective_at` 写入 time travel relation
- **AND** 用户可以通过时间轴查询看到 submitted/approved 的历史记录
