## ADDED Requirements

### Requirement: 类型继承定义
系统 SHALL 支持在 `om_type` 上定义单继承关系，每个类型最多有一个父类型（`parent_type`）。系统 SHALL 支持定义 mixin 接口，一个类型可以实现多个 mixin。mixin 仅提供属性定义的复用，不参与类型层级的 `isSubtypeOf` 判定。

#### Scenario: 定义单继承类型
- **GIVEN** 已存在类型 `Asset`
- **WHEN** 调用 `defineType(runner, 'ITAsset', 'IT资产', { parentType: 'Asset' })`
- **THEN** `om_type` 中创建 `ITAsset` 记录，`parent_type` 为 `Asset`

#### Scenario: 定义多层继承链
- **GIVEN** 已存在类型 `Asset` 和 `ITAsset`（parent_type=Asset）
- **WHEN** 调用 `defineType(runner, 'Server', '服务器', { parentType: 'ITAsset' })`
- **THEN** `Server` 的完整继承链为 `Server → ITAsset → Asset`

#### Scenario: 定义 mixin 接口
- **GIVEN** 已存在 mixin `Auditable`（定义了 `created_by`、`updated_at` 属性）
- **WHEN** 调用 `defineType(runner, 'Server', '服务器', { parentType: 'ITAsset', mixins: ['Auditable'] })`
- **THEN** `Server` 继承 `ITAsset` 的属性和关系，同时获得 `Auditable` 的属性定义

#### Scenario: 防止循环继承
- **GIVEN** 已存在类型 `A`（parent_type=B）和 `B`
- **WHEN** 调用 `defineType` 将 `B` 的 parent_type 设为 `A`
- **THEN** 系统抛出错误，提示检测到循环继承

#### Scenario: 父类型不存在
- **GIVEN** 不存在类型 `NonExistent`
- **WHEN** 调用 `defineType(runner, 'Child', '子类型', { parentType: 'NonExistent' })`
- **THEN** 系统抛出错误，提示父类型不存在

### Requirement: 属性继承与收紧约束
系统 SHALL 支持子类型自动继承父类型及 mixin 的所有属性定义。子类型 SHALL 可以收紧继承的属性约束（如将 `required: false` 改为 `required: true`），但 SHALL NOT 允许放宽约束或更改 `value_type`。

#### Scenario: 子类型继承父类型属性
- **GIVEN** 类型 `Asset` 定义了属性 `name`(String, required) 和 `location`(String, optional)
- **AND** 类型 `ITAsset` 继承自 `Asset`
- **WHEN** 调用 `getAttributeDefinitions(runner, 'ITAsset')`
- **THEN** 返回结果包含 `name`(String, required) 和 `location`(String, optional)
- **AND** 还包含 `ITAsset` 自身定义的属性

#### Scenario: 子类型收紧属性约束
- **GIVEN** 类型 `Asset` 定义了属性 `location`(String, optional)
- **AND** 类型 `ITAsset` 继承自 `Asset`
- **WHEN** 调用 `defineAttribute(runner, 'ITAsset', 'location', 'String', true)`
- **THEN** `ITAsset` 的 `location` 属性变为 required
- **AND** `Asset` 的 `location` 属性仍为 optional

#### Scenario: 禁止放宽约束
- **GIVEN** 类型 `Asset` 定义了属性 `name`(String, required)
- **AND** 类型 `ITAsset` 继承自 `Asset`
- **WHEN** 调用 `defineAttribute(runner, 'ITAsset', 'name', 'String', false)`
- **THEN** 系统抛出错误，提示不允许将 required 从 true 放宽为 false

#### Scenario: 禁止更改属性类型
- **GIVEN** 类型 `Asset` 定义了属性 `name`(String)
- **AND** 类型 `ITAsset` 继承自 `Asset`
- **WHEN** 调用 `defineAttribute(runner, 'ITAsset', 'name', 'Number', true)`
- **THEN** 系统抛出错误，提示不允许更改继承属性的 value_type

### Requirement: 关系继承
系统 SHALL 支持子类型自动继承父类型上定义的关系。当 `om_rel_def` 中 `from_type` 或 `to_type` 为某类型时，该类型的所有子类型 SHALL 自动适用该关系定义。`validateRelation` SHALL 递归检查类型继承链。

#### Scenario: 子类型继承父类型的出边关系
- **GIVEN** 关系 `owned_by` 定义为 `Asset → Organization`
- **AND** `ITAsset` 继承自 `Asset`
- **WHEN** 调用 `linkEntities(runner, 'server-01', 'owned_by', 'org-01')` 其中 `server-01` 类型为 `ITAsset`
- **THEN** 关系创建成功，`validateRelation` 通过

#### Scenario: 子类型继承父类型的入边关系
- **GIVEN** 关系 `manages` 定义为 `Person → Asset`
- **AND** `Server` 继承自 `ITAsset` 继承自 `Asset`
- **WHEN** 调用 `linkEntities(runner, 'admin-01', 'manages', 'server-01')` 其中 `server-01` 类型为 `Server`
- **THEN** 关系创建成功

### Requirement: 多态查询
系统 SHALL 支持按父类型查询时返回所有子类型的实例。`findByType` 默认包含子类型实例，可通过 `exact: true` 选项仅查询精确类型。`aggregateByType` 同样支持多态聚合。

#### Scenario: findByType 默认多态查询
- **GIVEN** 存在 `Asset` 类型实例 2 个，`ITAsset` 实例 3 个，`Server` 实例 1 个
- **AND** `ITAsset` 继承自 `Asset`，`Server` 继承自 `ITAsset`
- **WHEN** 调用 `findByType(runner, 'Asset')`
- **THEN** 返回 6 个实例（2 + 3 + 1）

#### Scenario: findByType 精确查询
- **GIVEN** 同上数据
- **WHEN** 调用 `findByType(runner, 'Asset', {}, { exact: true })`
- **THEN** 仅返回 2 个 `Asset` 类型实例

#### Scenario: aggregateByType 多态聚合
- **GIVEN** `Asset` 及其子类型实例均有 `value` 属性
- **WHEN** 调用 `aggregateByType(runner, 'Asset', 'value', 'sum')`
- **THEN** 返回所有 Asset 及子类型实例的 value 总和

### Requirement: 继承链查询 API
系统 SHALL 提供查询类型继承关系的 API：`getTypeHierarchy` 返回完整继承树，`getAncestors` 返回祖先链，`getDescendants` 返回所有后代类型，`isSubtypeOf` 判断类型关系。

#### Scenario: 获取完整继承树
- **GIVEN** 存在继承链 `Server → ITAsset → Asset` 和 `Vehicle → Asset`
- **WHEN** 调用 `getTypeHierarchy(runner)`
- **THEN** 返回以根类型为起点的树结构，包含所有类型及其子类型

#### Scenario: 获取祖先链
- **GIVEN** 存在继承链 `Server → ITAsset → Asset`
- **WHEN** 调用 `getAncestors(runner, 'Server')`
- **THEN** 返回 `['ITAsset', 'Asset']`（从近到远）

#### Scenario: 获取后代类型
- **GIVEN** 存在继承链 `Server → ITAsset → Asset` 和 `Laptop → ITAsset`
- **WHEN** 调用 `getDescendants(runner, 'Asset')`
- **THEN** 返回 `['ITAsset', 'Server', 'Laptop']`

#### Scenario: 判断子类型关系
- **GIVEN** 存在继承链 `Server → ITAsset → Asset`
- **WHEN** 调用 `isSubtypeOf(runner, 'Server', 'Asset')`
- **THEN** 返回 `true`

### Requirement: Schema 迁移兼容
系统 SHALL 保持对现有无继承 schema 的完全向后兼容。`om_type` 表新增 `parent_type` 列默认为 `null`。现有 `defineType(runner, name, description)` 调用签名保持不变，新签名通过第四个 options 参数传入 `parentType` 和 `mixins`。`initSchema` SHALL 新增 `om_mixin` 和 `om_type_mixin` 存储关系。

#### Scenario: 现有代码无需修改
- **GIVEN** 现有代码调用 `defineType(runner, 'Supplier', '供应商')`
- **WHEN** 升级到新版本后执行
- **THEN** 行为与之前完全一致，`parent_type` 为 null

#### Scenario: initSchema 创建新关系
- **WHEN** 调用 `initSchema(runner)`
- **THEN** 除现有 6 张表外，额外创建 `om_mixin`（mixin 定义）和 `om_type_mixin`（类型-mixin 关联）

### Requirement: 继承可视化 Demo
系统 SHALL 在 cozo-lib-bun-viz 中新增一个 IT 资产管理 demo，展示类型继承链、多态查询、schema 继承图。demo 包含 `Asset → ITAsset → Server/Laptop` 和 `Asset → Vehicle` 的继承结构，以及 mixin `Auditable` 的使用。

#### Scenario: 加载继承 Demo
- **GIVEN** 用户访问 cozo-lib-bun-viz 的本体 demo 页面
- **WHEN** 选择 "IT资产管理（继承）" demo
- **THEN** 工作簿加载包含继承关系的类型定义、属性定义、实体数据
- **AND** schema 图中展示类型继承关系（用不同样式的边区分继承和普通关系）

#### Scenario: 多态查询展示
- **GIVEN** 已加载 IT 资产管理 demo 数据
- **WHEN** 执行 "按父类型查询所有资产" 查询
- **THEN** 结果表格包含 Asset、ITAsset、Server、Laptop、Vehicle 的所有实例
- **AND** 每行显示实例的实际类型

### Requirement: 属性与关系描述字段
系统 SHALL 支持为属性定义与关系定义写入可选的 `description` 文本，用于承载业务语义说明。该能力 SHALL 与现有 schema 向后兼容：不要求对既有 `om_attr_def` / `om_rel_def` 做 schema 迁移。

#### Scenario: 写入并读取属性 description
- **GIVEN** 已定义类型 `Asset`
- **WHEN** 调用 `defineAttribute(runner, 'Asset', 'asset_tag', 'String', true, '资产编号')`
- **THEN** `getAttributeDefinitions(runner, 'Asset').get('asset_tag')` 包含 `description='资产编号'`

#### Scenario: 继承链上的 description 优先级
- **GIVEN** `Asset` 定义属性 `name` description 为 "资产名称"
- **AND** `ITAsset` 继承自 `Asset` 并定义同名属性 `name` description 为 "IT资产名称"
- **WHEN** 调用 `getAttributeDefinitions(runner, 'Server')`（其中 `Server → ITAsset → Asset`）
- **THEN** `name` 的 `description` 为 "IT资产名称"

#### Scenario: 写入关系 description
- **GIVEN** 已定义类型 `Asset`
- **WHEN** 调用 `defineRelation(runner, 'depends_on', 'Asset', 'Asset', true, '依赖关系')`
- **THEN** 系统保存该关系的 description

### Requirement: Demo 使用中文描述
系统 SHALL 将本次新增示例（cozo-lib-bun CLI demo 与 cozo-lib-bun-viz IT 资产 demo）中的类型描述改为中文，并在属性定义与关系定义中提供中文 description。
系统 SHOULD 将 cozo-lib-bun-viz 中所有本体 demo（采购、人力、CRM、IT 资产）统一为相同列结构，并确保 `description` 永远位于最后一列。

#### Scenario: 可视化 demo 表结构包含 description 列
- **GIVEN** 用户加载 "IT资产管理（继承）" demo
- **WHEN** 查看工作簿的 "属性定义" 与 "关系定义" sheet
- **THEN** 两个 sheet 的列中包含 `description`
- **AND** 行数据中的 description 为中文

#### Scenario: 采购/人力/CRM demo 表结构包含 description 列
- **GIVEN** 用户加载采购、人力或 CRM demo
- **WHEN** 查看工作簿的 "属性定义" 与 "关系定义" sheet
- **THEN** 两个 sheet 的列中包含 `description`

#### Scenario: 类型/属性/关系 description 均为最后一列
- **GIVEN** 用户加载任意本体 demo
- **WHEN** 查看 "类型定义"、"属性定义"、"关系定义" sheet 的列顺序
- **THEN** `description` 始终是最后一列
