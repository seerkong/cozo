# 变更：为 cozo-om 添加类型继承与多态支持

## 背景和动机 (Context And Why)
当前 cozo-om 的 `om_type` 是扁平结构，无法表达类型间的继承关系。这导致属性定义无法复用、关系定义无法泛化、查询无法按父类型聚合子类型实例。类型继承是从"带类型的 property graph"进化到"真正的本体论系统"的分水岭，也是模拟 Palantir Foundry Ontology 最关键的缺失能力。

## "要做"和"不做" (Goals / Non-Goals)
**目标:**
- 在 `om_type` 上实现单继承（每个类型最多一个 parent_type）
- 支持 mixin 接口机制，实现属性定义的横向复用
- 子类型自动继承父类型的属性定义和关系定义
- 子类型可收紧继承属性的约束（required: false → true），但不可放宽或改类型
- `findByType`、`aggregateByType` 默认支持多态查询（包含子类型实例）
- 提供继承链查询 API（getTypeHierarchy、getAncestors、getDescendants、isSubtypeOf）
- 保持对现有无继承 schema 的完全向后兼容
- 在 cozo-lib-bun 中编写完整测试用例
- 在 cozo-lib-bun-viz 中新增 IT 资产管理继承 demo

**非目标:**
- 不实现多继承（一个类型多个 parent_type）
- 不实现 Action/Function 行为层（属于后续 Phase 2）
- 不实现时间维度/bi-temporal 支持（属于后续 Phase 3）
- 不实现 schema 版本化与迁移框架（属于后续 Phase 4）
- 不修改 cozo-dsl.js 的 API

## 变更内容（What Changes）
- **BREAKING** `om_type` 存储关系新增 `parent_type` 列（默认 null，向后兼容）
- `initSchema` 新增创建 `om_mixin`（mixin 定义）和 `om_type_mixin`（类型-mixin 关联）存储关系
- `defineType` 签名扩展：新增可选第四参数 `options: { parentType?, mixins? }`
- `getAttributeDefinitions` 改为递归解析继承链 + mixin 的属性定义
- `validateRelation` 改为递归检查类型继承链的关系匹配
- `findByType` 默认包含子类型实例，新增 `exact` 选项
- `aggregateByType` 默认包含子类型实例
- 新增 API：`getTypeHierarchy`、`getAncestors`、`getDescendants`、`isSubtypeOf`、`defineMixin`
- `defineAttribute`/`defineRelation` 支持可选的 `description` 参数，用于记录业务语义说明（向后兼容）
- `initSchema` 新增创建 `om_attr_desc` / `om_rel_desc` 以保存 description（避免对既有关系表做 schema 迁移）
- cozo-lib-bun-viz server 新增 IT 资产管理 demo 数据和查询定义
- cozo-lib-bun-viz frontend schema 图支持继承边的差异化展示
- 新增示例文案：本次新增 demo 的类型描述改为中文，并在属性/关系定义中提供中文 description
- cozo-lib-bun-viz：采购/人力/CRM demo 的表结构与 IT 资产 demo 对齐（类型/属性/关系定义均包含 description 且位于最后一列）

## 影响范围（Impact）
- 受影响的功能规范：cozo-om 核心库（schema 定义、属性解析、关系校验、查询）
- 受影响的文件：
  - `cozo-lib-bun/cozo-om.js` — 核心变更
  - `cozo-lib-bun/` 测试文件 — 新增继承相关测试
  - `cozo-lib-bun-viz/server/` — 新增 demo 数据源
  - `cozo-lib-bun-viz/frontend/` — schema 图继承边展示
