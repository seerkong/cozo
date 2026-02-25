# OOP Prototype Constraint Design

## 1. 目标

在 `oop-prototype.js` 的对象建模原型上增加可执行约束，避免“数据写进去但语义不合法”的情况。

当前覆盖三类约束：

- 属性类型检查（`String` / `Number` / `Bool` / `Json`）
- 关系类型合法性检查（`from_type -> to_type`）
- 必填属性检查（`required: true`）

## 2. 数据模型

原型使用如下 relation：

- `om_type {name => description}`：对象类型定义
- `om_attr_def {type_name, attr_name => value_type, required}`：属性定义
- `om_rel_def {rel_name => from_type, to_type, directed}`：关系定义
- `om_entity {id => type_name, label}`：对象实例
- `om_property {entity_id, attr_name => value}`：动态属性（EAV）
- `om_edge {from_id, rel_name, to_id => props}`：对象关系边

## 3. 约束语义

### 3.1 属性类型检查

- 在写入 `om_property` 前校验。
- 读取实体类型：`om_entity.id -> type_name`。
- 读取属性定义：`om_attr_def(type_name, attr_name)`。
- 规则：
  - `String` 对应 `typeof value === 'string'`
  - `Number` 对应 `typeof value === 'number' && Number.isFinite(value)`
  - `Bool` 对应 `typeof value === 'boolean'`
  - `Json` 对应 `value !== null && typeof value === 'object'`

### 3.2 关系类型合法性检查

- 在写入 `om_edge` 前校验。
- 读取两端实体类型：
  - `from_id -> from_type`
  - `to_id -> to_type`
- 读取关系定义：`om_rel_def(rel_name) -> expected_from_type, expected_to_type`。
- 校验 `from_type == expected_from_type && to_type == expected_to_type`。

### 3.3 必填属性检查

- 通过显式 `finalizeEntity(entityId)` 执行。
- 读取类型要求的必填属性：`om_attr_def.required == true`。
- 读取实体已设置属性：`om_property(entity_id)`。
- 对比差集；若缺失则抛错。

## 4. API 设计

新增函数：

- `inferValueType(value): 'String' | 'Number' | 'Bool' | 'Json' | 'Unknown'`
- `getEntityType(db, entityId): Promise<string>`
- `validatePropertyType(db, entityId, attrName, value): Promise<void>`
- `validateRelation(db, fromId, relName, toId): Promise<void>`
- `validateRequiredProperties(db, entityId): Promise<string[]>`
- `finalizeEntity(db, entityId): Promise<void>`

修改函数：

- `setProperty(...)`：先调用 `validatePropertyType(...)` 再 `:put om_property`
- `linkEntities(...)`：先调用 `validateRelation(...)` 再 `:put om_edge`

## 5. 校验时机

- 写入时检查：
  - `setProperty` → 类型与属性定义
  - `linkEntities` → 关系定义与端点类型
- 完整性检查：
  - `finalizeEntity` 在“对象准备提交/发布”时调用

说明：`createEntity` 阶段不强制必填属性，允许“先建对象、后逐步补属性”。

## 6. 错误约定

统一使用 `throw new Error(message)`，消息模板包括：

- `Entity '<id>' does not exist`
- `Attribute '<attr>' is not defined for type '<type>'`
- `Type mismatch: attribute '<attr>' expects <Expected>, got <Actual>`
- `Relation '<rel>' is not defined`
- `Relation '<rel>' expects <From> -> <To>, got <ActualFrom> -> <ActualTo>`
- `Entity '<id>' is missing required properties: <a>, <b>, ...`

## 7. 演示场景

`main()` 包含以下检查演示：

- 属性类型错误（`priority` 传 `Number`）
- 未定义属性（`User.age`）
- 关系端点类型错误（`Task owns User`）
- 未定义关系（`manages`）
- 缺失必填属性（新建 `Task` 未设置 `priority`）
- 补齐后通过必填校验

## 8. 已知限制与后续

当前版本限制：

- 仅做运行时校验，未提供批量导入事务模板
- 未实现枚举值/范围/正则等更细粒度约束
- 未实现删除约束（如防止 dangling edge）

后续可扩展：

- 增加 `validateDeleteEntity`（先查入边/出边）
- 增加 `enum`、`min/max`、`pattern` 元数据并统一校验
- 将“写入 + 校验”组合为单脚本事务模板（更强一致性）
