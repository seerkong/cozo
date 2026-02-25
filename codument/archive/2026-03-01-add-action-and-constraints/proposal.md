# 变更：为 cozo-om 添加 Action/Mutation 行为层与复合约束引擎

## 背景和动机 (Context And Why)

cozo-om 在 Phase 1 完成了类型继承与多态支持，已具备完整的 Schema/TBox 和 Instance/ABox 能力。但当前只有数据层，没有行为层——类型只定义结构，不定义行为。这是与 Palantir Ontology 最大的差距之一。同时，现有的 validation 仅支持单点校验（类型检查、必填检查），缺少跨实体约束、条件约束和派生规则。

Phase 2 的目标是让 cozo-om 从"带类型的 property graph"进化为"具备行为与规则的本体论系统"。

## "要做"和"不做" (Goals / Non-Goals)

**目标:**
- 实现 Action/Mutation 模式：Action 是业务级事件，Mutation 是原子数据变更，Action handler 将 Action 转换为 Mutation 列表
- 支持 Action 的继承、覆盖与扩展（callParentAction / super 模式）
- 实现 before/after 拦截器（Interceptor），before 可阻止 Action 执行
- 实现三类复合约束：条件约束（单实体多属性联合）、跨实体约束（聚合/唯一性）、派生规则（computed property）
- 约束校验支持两种模式：写入时同步校验（默认）+ 显式调用，并提供 skipConstraints 选项
- 约束与 Action 均支持类型继承
- 保持对现有无 Action/Constraint 的 schema 完全向后兼容
- 在 cozo-lib-bun 中编写完整测试用例（覆盖率 >80%）
- 在 cozo-lib-bun-viz 中新增独立 demo 展示 Action 与约束能力

**非目标:**
- 不实现异步 Action（如消息队列、事件总线）
- 不实现 Action 的权限控制（属于后续 Phase 4 权限集成）
- 不在 cozo-om 核心 API 中引入 bi-temporal 建模框架（demo 可使用 CozoDB 自带 time travel relation 做展示）
- 不实现 Schema 版本化与迁移框架（属于 Phase 4）
- 不修改 cozo-dsl.js 的 API

## 变更内容（What Changes）

- `initSchema` 新增存储关系：`om_action_def`、`om_mutation_def`、`om_constraint_def`、`om_computed_def`、`om_interceptor_def`
- 新增 API：`defineAction`、`defineMutation`、`executeAction`、`callParentAction`
- 新增 API：`addInterceptor`
- 新增 API：`defineConstraint`、`validateConstraints`
- 新增 API：`defineComputed`
- 修改 `setProperty`：增加 `options.skipConstraints` 参数，默认触发条件约束校验
- 修改 `linkEntities`：增加 `options.skipConstraints` 参数，默认触发跨实体约束校验
- 修改 `getEntityView`、`getProperty`：支持返回派生属性
- Action/Mutation/Constraint/Computed 均支持类型继承链解析
- `cozo-om.d.ts` 更新所有新增/修改函数的 TypeScript 声明
- 新增 cozo-lib-bun-viz demo：展示审批流 Action、条件约束、跨实体约束、派生属性

## 影响范围（Impact）

- 受影响的功能规范：`codument/specs/cozo-om/spec.md`
- 受影响的文件：
  - `cozo-lib-bun/cozo-om.js` — 核心变更：initSchema 扩展、Action/Mutation/Constraint/Computed 全部新增逻辑
  - `cozo-lib-bun/cozo-om.d.ts` — TypeScript 类型声明更新
  - `cozo-lib-bun/__tests__/` — 新增 Action/Mutation/Constraint/Computed 测试文件
  - `cozo-lib-bun/package.json` — 可能新增 demo script
  - `cozo-lib-bun-viz/server/src/demos/` — 新增 demo 数据源
  - `cozo-lib-bun-viz/server/src/index.js` — 适配新 demo
  - `cozo-lib-bun-viz/server/src/demos/index.js` — 注册新 demo
