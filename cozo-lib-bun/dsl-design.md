# Cozo Internal DSL Design

## 1. 目标

在 `cozo-lib-bun` 提供一个语言内 DSL（`cozo-dsl.js`），用于**安全、可组合地动态构造 Cozo 查询**，避免手写字符串拼接带来的维护和注入风险。

输出统一为：

- `script`：Cozo 查询脚本字符串
- `params`：参数对象（交给 `db.run(script, params)`）

## 2. 设计原则

- **参数和值分离**：值必须通过参数进入 `params`，不直接插值到 `script`。
- **标识符校验**：relation/column/variable 名必须通过校验。
- **组合优先**：通过链式 Builder 拼装常见查询片段。
- **渐进覆盖**：先覆盖 `cozo-om` 高频模式，再扩展完整语法。

## 3. 核心 API

### 3.1 构建入口

- `dsl.query()` -> `CozoQueryBuilder`

### 3.2 token

- `dsl.var(name)`：变量 token
- `dsl.param(name, value)`：命名参数 token（`dsl.p` 为兼容别名）
- `dsl.val(value)`：自动参数名 token
- `dsl.raw(text)`：原始文本（仅用于已知安全场景）

### 3.3 builder 片段

- `input(bindings)` -> `?[...] <- [[...]]`
- `select(columns)` + `fromStored(name, bindings)` -> `?[...] := *rel{...}`
- `put/insert/update/rm/create(...)` -> mutation directive
- `order(...cols)`, `limit(n)`, `offset(n)`
- `build()` -> `{ script, params }`
- `execute(runner)` -> `runner.run(script, params)`

### 3.4 条件表达式（v2）

- 比较：`eq`, `neq`, `gt`, `lt`, `gte`, `lte`
- 组合：`and(...expr)`, `or(...expr)`, `not(expr)`
- 成员判断：`in(left, values)`（内部编译为 `is_in(left, [...])`）
- 入口：`builder.where(expr)`

示例：

```js
const built = dsl
  .query()
  .select(['id', 'hours'])
  .fromStored('om_property', {
    entity_id: dsl.var('id'),
    attr_name: dsl.param('attr', 'estimate_hours'),
    value: dsl.var('hours'),
  })
  .where(
    dsl.and(
      dsl.gt(dsl.var('hours'), dsl.param('minHours', 15)),
      dsl.in(dsl.var('id'), [dsl.param('a', 't:api'), dsl.param('b', 't:web')]),
      dsl.not(dsl.lt(dsl.var('hours'), dsl.param('zero', 0)))
    )
  )
  .build();
```

## 4. 安全模型

### 4.1 值安全

值统一通过参数写入：

- `dsl.param('id', 'u:alice')`（或 `dsl.p('id', 'u:alice')`）-> 脚本里 `$id`，值放到 `params.id`
- 普通值（string/number/bool/object）默认自动参数化（`_pN`）

### 4.2 标识符安全

使用正则校验标识符：

- `^[A-Za-z_][A-Za-z0-9_]*$`

非法标识符抛 `DslError('DSL101', ...)`。

### 4.3 错误码

`DslError` 提供结构化错误码，当前包含：

- `DSL101`~`DSL124`（标识符错误、空查询、参数冲突、无效 limit/offset、条件表达式编译错误等）

## 5. 与 om-lib 集成

`cozo-om.js` 已采用 DSL 改造部分高频函数：

- schema 初始化：`initSchema`
- 类型定义：`defineType` / `defineAttribute` / `defineRelation`
- 实体写入：`createEntity` / `upsertEntity`
- 基础查询：`getEntityType` / `getAttributeDefinitions` / `getProperty` / `getAllProperties`
- 关系写入：`setProperty` / `linkEntities` / `validateRelation`

并且在当前版本中，`cozo-om.js` 的查询构造路径已经统一迁移到 DSL（不再依赖手写 Cozo 查询字符串模板）。

这保证了现有 `om` API 不变，同时内部查询构造更一致。

## 6. 使用示例

```js
const { CozoDb, dsl } = require('cozo-lib-bun');

const db = new CozoDb();
const built = dsl
  .query()
  .select(['type_name'])
  .fromStored('om_entity', {
    id: dsl.param('id', 'u:alice'),
    type_name: dsl.var('type_name'),
    label: dsl.var('_label'),
  })
  .limit(1)
  .build();

const result = await db.run(built.script, built.params);
```

## 7. 后续扩展

- 增加更丰富逻辑表达式（例如 `between` / `like`）
- 增加固定规则构造器（例如排序/分页模板）
- 增加规则命名和多规则拼装能力
- 条件表达式做 DNF 编译优化与分支上限控制
