## 上下文

cozo-om 当前有 31 个导出函数，核心数据模型为 10 张存储关系（om_type/om_mixin/om_type_mixin/om_attr_def/om_rel_def/om_entity/om_property/om_edge/om_attr_desc/om_rel_desc）。Phase 1 已实现类型继承与多态。

本次变更需要在保持向后兼容的前提下，引入 Action/Mutation 行为层和复合约束引擎。所有新增逻辑均在 JS 层实现（cozo-om.js），不修改 CozoDB 引擎或 cozo-dsl.js。

约束：
- Action/Mutation/Interceptor 的 handler 是 JS 函数，注册在内存中（非持久化到 CozoDB 关系）
- 约束定义的元数据（名称、类型、message）持久化到 CozoDB 关系，但校验逻辑（when/then 函数）注册在内存中
- 派生属性的计算函数注册在内存中，不持久化
- 每次 `initSchema` 后需要重新注册 Action/Mutation/Interceptor/Constraint handler/Computed function

## 方案概览

1. **内存注册表（Registry）**
   - 新增模块级 `Map` 注册表，按 typeName 索引：
     - `_actionRegistry: Map<string, Map<string, ActionDef>>` — Action handler
     - `_mutationRegistry: Map<string, Map<string, MutationDef>>` — Mutation executor
     - `_interceptorRegistry: Map<string, Map<string, {before: fn[], after: fn[]}>>` — 拦截器
     - `_constraintRegistry: Map<string, Map<string, ConstraintDef>>` — 约束校验函数
     - `_computedRegistry: Map<string, Map<string, ComputeFn>>` — 派生属性计算函数
   - 注册表在 `initSchema` 时不清空（允许先注册再 initSchema，也允许反过来）
   - 提供 `clearRegistry()` 用于测试场景

2. **Action/Mutation 执行流程**
   ```
   executeAction(runner, entityId, actionName, params)
     │
     ├─ 1. 解析 Action 定义（MRO：自身 → 父类型 → 祖先）
     ├─ 2. 构建 ActionContext（runner, entityId, params, getProperty, setProperty...）
     ├─ 3. 执行 before 拦截器链（任一抛错则中止）
     ├─ 4. 调用 Action handler(ctx) → Mutation[]
     ├─ 5. 按顺序执行每个 Mutation
     │     └─ 每个 Mutation: mutationRegistry[typeName][mutationName].executor(ctx, params)
     ├─ 6. 执行 after 拦截器链
     └─ 7. 返回执行结果
   ```
   - Mutation 执行失败时的回滚策略：由于 CozoDB 单次 `run()` 是原子的，但多次 `run()` 之间无事务，采用"补偿式回滚"——记录每个 Mutation 执行前的属性快照，失败时逆序恢复
   - `callParentAction(ctx, actionName, params)`：从当前类型的父类型开始向上查找 Action 定义，调用其 handler 返回 Mutation 列表（不执行），供子类 handler 合并

3. **ActionContext 对象**
   ```js
   {
     runner,           // CozoDB runner
     entityId,         // 当前实体 ID
     typeName,         // 当前实体类型
     params,           // Action 参数
     getProperty,      // (attrName) => Promise<value>
     setProperty,      // (attrName, value) => Promise<void>  (内部调用，跳过约束)
     linkEntities,     // (relName, toId) => Promise<void>
     getNeighbors,     // (relName, direction?) => Promise<Entity[]>
     callParentAction, // (actionName, params) => Promise<Mutation[]>
   }
   ```

4. **复合约束引擎**
   - 约束元数据持久化：`om_constraint_def` 关系 `keys: [type_name, constraint_name], values: [constraint_type, message]`
     - `constraint_type`: `'conditional'` | `'cross-entity'` | `'computed-dep'`
   - 约束校验函数注册在 `_constraintRegistry`，签名：`async (ctx) => { valid: boolean, error?: string }`
   - `ConstraintContext` 类似 ActionContext，提供 getProperty/getNeighbors/getComputed 等读取能力
   - 校验触发点：
     - `setProperty` 默认调用该实体类型的所有 conditional 约束（可通过 `{ skipConstraints: true }` 跳过）
     - `linkEntities` 默认调用 from 实体类型的所有 cross-entity 约束（可通过 `{ skipConstraints: true }` 跳过）
     - `validateConstraints(runner, entityId)` 显式调用所有约束（含继承）
   - 约束继承：`validateConstraints` 收集实体类型 + 所有祖先类型 + mixin 的约束，全部执行

5. **派生属性（Computed Property）**
   - 元数据持久化：`om_computed_def` 关系 `keys: [type_name, attr_name], values: [description]`
   - 计算函数注册在 `_computedRegistry`，签名：`async (ctx) => value`
   - `getProperty` 改造：先查 `om_property`，未找到则查 `_computedRegistry`，有则调用计算函数
   - `getEntityView` 改造：在返回 properties 后，追加所有已注册的 computed 属性
   - `setProperty` 改造：如果目标属性是 computed，抛出错误
   - 派生属性继承：子类型继承父类型的 computed 定义，子类型可覆盖

6. **Schema 扩展**
   - `initSchema` 新增：
     - `om_action_def`: `keys: [type_name, action_name], values: [description]`
     - `om_mutation_def`: `keys: [type_name, mutation_name], values: [description]`
     - `om_interceptor_def`: `keys: [type_name, action_name, phase, seq], values: [description]`
     - `om_constraint_def`: `keys: [type_name, constraint_name], values: [constraint_type, message]`
     - `om_computed_def`: `keys: [type_name, attr_name], values: [description]`
   - 元数据关系用于持久化"定义了什么"，实际逻辑在内存注册表

7. **Viz Demo：审批流**
   - 新增 `cozo-lib-bun-viz/server/src/demos/approval-flow.js`
   - 业务场景：采购审批流
     - 类型：`ApprovalRequest`（继承自某基类）、`Approver`、`Department`
     - Action：`submit`、`approve`、`reject`、`escalate`
     - Mutation：`setStatus`、`addComment`、`assignApprover`
     - 条件约束：`status='submitted'` 时才能 `approve`
     - 跨实体约束：每个 Department 最多 3 个 pending request
     - 派生属性：`approval_chain_length`（关联审批人数量）
   - 查询：展示 Action 执行结果、约束校验、派生属性计算

## 影响范围与修改点（Impact）

| 文件 | 变更类型 | 说明 |
|---|---|---|
| `cozo-lib-bun/cozo-om.js` | 修改 | 核心变更：initSchema 扩展、内存注册表、Action/Mutation/Interceptor/Constraint/Computed 全部新增；setProperty/linkEntities/getProperty/getEntityView 改造 |
| `cozo-lib-bun/cozo-om.d.ts` | 修改 | TypeScript 类型声明更新 |
| `cozo-lib-bun/__tests__/*.test.js` | 新增 | Action/Mutation/Interceptor/Constraint/Computed 测试文件 |
| `cozo-lib-bun/package.json` | 修改 | 可能新增 demo script |
| `cozo-lib-bun-viz/server/src/demos/approval-flow.js` | 新增 | 审批流 demo |
| `cozo-lib-bun-viz/server/src/demos/index.js` | 修改 | 注册新 demo |
| `cozo-lib-bun-viz/server/src/index.js` | 修改 | 适配 Action/Constraint demo 的 API 调用 |

## 决策

- **决策：Action/Mutation/Interceptor/Constraint/Computed 的 handler 函数注册在 JS 内存中，不序列化到 CozoDB**
  - 原因：JS 函数无法序列化为 Datalog 数据；CozoDB 关系仅存储元数据（名称、类型、描述），实际逻辑由应用层注册
  - 替代方案：将约束表达式编译为 CozoScript 字符串存储 — 灵活性不足，无法表达复杂 JS 逻辑
  - 权衡：每次应用启动需重新注册 handler，但这与 cozo-om 的使用模式一致（viz server 每次请求创建新 DB + 注册）

- **决策：Mutation 回滚采用补偿式（snapshot + restore），不依赖 CozoDB 事务**
  - 原因：CozoDB 的 `run()` 是单脚本原子的，但跨多次 `run()` 无事务支持；补偿式回滚在 JS 层可控
  - 替代方案：将所有 Mutation 合并为单次 CozoScript 执行 — 复杂度高，且 Mutation executor 可能需要中间读取
  - 风险：补偿回滚在并发场景下可能不完美 → 缓解：cozo-om 当前为单用户嵌入式场景，并发风险低

- **决策：派生属性 lazy 计算，不持久化**
  - 原因：避免维护依赖图和触发更新的复杂性；读取时计算保证数据一致性
  - 替代方案：eager 计算 + 写入时更新 — 性能更优但实现复杂度高
  - 权衡：频繁读取同一 computed 属性时有性能开销 → 缓解：可在 ActionContext 中缓存单次请求内的计算结果

- **决策：约束校验默认同步触发，提供 skipConstraints 逃生舱**
  - 原因：保证数据一致性为默认行为；批量导入等场景需要跳过以提升性能
  - 替代方案：仅显式调用 — 容易遗漏导致数据不一致

## 风险 / 权衡

- **性能风险：setProperty 每次触发约束校验** → 缓解：仅触发与被修改属性相关的约束（按属性名过滤）；skipConstraints 逃生舱
- **复杂度风险：Action 继承 + 覆盖 + callParentAction 的 MRO 解析** → 缓解：复用 Phase 1 的 `_getAncestorList`，逻辑与属性继承一致
- **兼容性风险：setProperty/linkEntities 签名变更** → 缓解：新增 options 参数为可选，不影响现有调用
- **内存注册表生命周期** → 缓解：提供 `clearRegistry()` 用于测试；文档说明注册时机

## 待解决问题

- 约束的 `when`/`then` 函数是否需要支持声明式 DSL（如 `{ attr: 'status', eq: 'active' }`）作为 JS 函数的替代？（当前设计：仅 JS 函数，后续可扩展）
- 跨实体约束在 `ingestBatch` 中的触发时机：每条记录触发 vs 批量结束后统一触发？（当前设计：批量结束后统一触发）
- 是否需要为 Action 执行结果提供结构化返回值（如 `{ success, mutations, errors }`）？（当前设计：成功返回 void，失败抛错）
