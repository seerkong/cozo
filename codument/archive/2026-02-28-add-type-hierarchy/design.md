## 上下文

cozo-om 当前使用 6 张扁平存储关系实现本体建模，`om_type` 无继承能力。本次变更需要在保持向后兼容的前提下，引入单继承 + mixin 机制，并让属性解析、关系校验、多态查询全部感知继承链。

约束：
- CozoDB Datalog 引擎支持递归规则，可用于继承链解析
- cozo-om.js 当前通过 cozo-dsl 构建所有查询，不使用原始 Cozo 脚本
- cozo-lib-bun 当前无测试基础设施，需从零搭建
- viz server 每次请求创建临时 DB，schema 定义从 spreadsheet 读取

## 方案概览

1. **数据模型扩展**
   - `om_type` 新增 `parent_type` 值列（默认 null）
     - 现有 `initSchema` 中 `create('om_type', ['name'], ['description'])` 改为 `create('om_type', ['name'], ['description', 'parent_type'])`
   - 新增 `om_mixin` 存储关系：`keys: [name], values: [description]`
   - 新增 `om_type_mixin` 存储关系：`keys: [type_name, mixin_name], values: []`

2. **继承链解析（核心算法）**
   - `getAncestors(runner, typeName)` — 循环查询 `om_type` 的 `parent_type`，逐级向上直到 null，返回有序祖先数组
   - `getDescendants(runner, typeName)` — 查询所有 `om_type` 记录，在内存中构建 parent→children 映射，从目标类型 BFS 收集后代
   - `isSubtypeOf(runner, childType, parentType)` — 调用 `getAncestors` 检查 parentType 是否在祖先链中
   - `getTypeHierarchy(runner)` — 查询所有 `om_type`，构建完整树结构返回
   - 不使用 CozoDB 递归 Datalog 规则，原因：继承链通常很浅（<10 层），JS 循环更简单可控，避免引入原始 Cozo 脚本绕过 DSL 层

3. **属性继承解析**
   - `getAttributeDefinitions(runner, typeName)` 改造：
     1. 调用 `getAncestors` 获取祖先链（从远到近：`[Asset, ITAsset]`）
     2. 查询每个 mixin 的属性定义（通过 `om_type_mixin` 关联）
     3. 按顺序合并：mixin 属性 → 最远祖先属性 → ... → 父类型属性 → 自身属性
     4. 同名属性后者覆盖前者（即子类型优先）
   - `defineAttribute` 增加收紧校验：
     1. 查询父类型链中是否存在同名属性
     2. 如存在：禁止更改 `value_type`；`required` 只允许 false→true，不允许 true→false
     3. 如不存在：正常创建

4. **关系继承校验**
   - `validateRelation(runner, fromId, relName, toId)` 改造：
     1. 获取 fromId 的 type_name 及其祖先链
     2. 获取 toId 的 type_name 及其祖先链
     3. 查询 `om_rel_def` 中 relName 的定义
     4. 检查 `from_type` 是否在 fromId 的类型链中（含自身），`to_type` 是否在 toId 的类型链中
     5. 任一匹配即通过

5. **多态查询**
   - `findByType(runner, typeName, filter, options)` 改造：
     1. 如果 `options.exact === true`，行为不变
     2. 否则调用 `getDescendants` 获取所有子类型名称（含自身）
     3. 对每个类型名称执行现有查询逻辑，合并结果
   - `aggregateByType` 同理，先收集所有子类型名称再聚合

6. **defineType 签名扩展**
   - 新签名：`defineType(runner, name, description, options?)`
   - `options.parentType` — 字符串，父类型名称
   - `options.mixins` — 字符串数组，mixin 名称列表
   - 校验逻辑：
     1. 如有 parentType，验证其存在于 `om_type`
     2. 检测循环继承（getAncestors 中检查是否出现自身）
     3. 如有 mixins，验证每个 mixin 存在于 `om_mixin`
     4. 写入 `om_type`（含 parent_type）
     5. 写入 `om_type_mixin` 关联记录

7. **Mixin 机制**
   - `defineMixin(runner, name, description)` — 写入 `om_mixin`
   - mixin 的属性通过 `defineAttribute` 定义，type_name 使用 mixin 名称
   - `getAttributeDefinitions` 解析时，通过 `om_type_mixin` 查找关联的 mixin，合并其属性
   - mixin 不参与 `isSubtypeOf` 判定，不影响关系继承

8. **测试基础设施**
   - 使用 Bun 内置测试运行器（`bun test`）
   - 测试文件放在 `cozo-lib-bun/__tests__/` 目录
   - 每个测试用例创建临时内存 DB，调用 `initSchema` 后执行
   - 测试文件结构：
     - `__tests__/om-type-hierarchy.test.js` — 继承定义、循环检测、祖先/后代查询
     - `__tests__/om-attr-inheritance.test.js` — 属性继承、收紧约束、mixin 属性合并
     - `__tests__/om-rel-inheritance.test.js` — 关系继承校验
     - `__tests__/om-polymorphic-query.test.js` — 多态 findByType、aggregateByType
     - `__tests__/om-mixin.test.js` — mixin 定义与使用
     - `__tests__/om-backward-compat.test.js` — 向后兼容性验证

9. **Viz Demo: IT 资产管理**
   - 新增 `cozo-lib-bun-viz/server/src/demos/it-asset.js`，遵循现有 demo 模块结构
   - 类型继承结构：`Asset → ITAsset → Server | Laptop`，`Asset → Vehicle`
   - Mixin：`Auditable`（created_by, updated_at）
   - 4 个查询：DSL 多态查询（所有资产）、影响分析、资产所有权树、资产价值风险热点
   - `defaultTables` 中"类型定义"sheet 新增 `parent_type` 列
    - `SchemaGraphView.vue` 增加继承边样式：虚线 + 不同颜色区分继承关系和普通关系

10. **属性/关系 description 支持（向后兼容）**
    - 不对既有 `om_attr_def` / `om_rel_def` 做 schema 迁移，避免已有数据库或工具链受影响
    - 新增存储关系：
      - `om_attr_desc`：`keys: [type_name, attr_name]`, `values: [description]`
      - `om_rel_desc`：`keys: [rel_name]`, `values: [description]`
    - `defineAttribute(..., description?)` / `defineRelation(..., description?)` 在 description 非空时写入对应关系
    - `getAttributeDefinitions` 读取并合并 description，继承优先级与 valueType/required 保持一致

## 影响范围与修改点（Impact）

| 文件 | 变更类型 | 说明 |
|---|---|---|
| `cozo-lib-bun/cozo-om.js` | 修改 | 核心变更：initSchema、defineType、getAttributeDefinitions、validateRelation、findByType、aggregateByType；新增 6+ 个函数 |
| `cozo-lib-bun/cozo-om.d.ts` | 修改 | TypeScript 类型声明更新 |
| `cozo-lib-bun/__tests__/*.test.js` | 新增 | 6 个测试文件 |
| `cozo-lib-bun/package.json` | 修改 | 添加 test script |
| `cozo-lib-bun-viz/server/src/demos/it-asset.js` | 新增 | IT 资产管理 demo |
| `cozo-lib-bun-viz/server/src/demos/index.js` | 修改 | 注册新 demo |
| `cozo-lib-bun-viz/server/src/helpers.js` | 修改 | parseSheetsIntoBatch 支持 parent_type 列 |
| `cozo-lib-bun-viz/server/src/index.js` | 修改 | defineOntology 流程支持 parent_type |
| `cozo-lib-bun-viz/frontend/src/components/SchemaGraphView.vue` | 修改 | 继承边差异化样式 |

## 决策

- **决策：继承链在 JS 层循环解析，不使用 CozoDB 递归 Datalog 规则**
  - 原因：继承链通常 <10 层，JS 循环足够高效；避免绕过 DSL 层引入原始 Cozo 脚本；调试更简单
  - 替代方案：CozoDB 内置递归规则（`?[x] := parent[x, y], ancestor[y]`）— 性能更优但增加 DSL 复杂度

- **决策：mixin 使用独立存储关系（om_mixin + om_type_mixin），不复用 om_type 的 parent_type**
  - 原因：mixin 语义不同于继承（不参与 isSubtypeOf、不影响关系继承），独立存储更清晰
  - 替代方案：在 om_type 中用 `is_mixin` 标记 — 会污染类型查询，增加过滤复杂度

- **决策：getAttributeDefinitions 合并顺序为 mixin → 远祖先 → 近祖先 → 自身**
  - 原因：子类型优先（后覆盖前），mixin 优先级最低，符合"类继承优先于接口"的直觉

## 风险 / 权衡

- **性能风险：多态查询 N+1** — `findByType` 对每个子类型执行独立查询 → 缓解：继承树通常很小（<20 类型），可接受；后续可优化为单次 Datalog 查询
- **兼容性风险：om_type schema 变更** — 新增 parent_type 列 → 缓解：默认 null，现有数据无需迁移，`initSchema` 幂等
- **复杂度风险：属性合并逻辑** — 多层继承 + mixin 的属性合并可能产生意外覆盖 → 缓解：收紧约束校验 + 完整测试覆盖

## 待解决问题

- mixin 是否需要支持继承其他 mixin？（当前设计：不支持，保持简单）
- 是否需要在 `getEntityView` 返回中标注属性来源（自身/继承/mixin）？（当前设计：不标注，后续按需添加）
