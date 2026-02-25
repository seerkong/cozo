# 变更：为 cozo-om 引入 Bi-temporal 时间维度

## 背景和动机 (Context And Why)

cozo-om 当前是一个无时间感知的本体建模层。CozoDB 引擎原生支持 `Validity` 类型和 `@ timestamp` 时间旅行查询，但 om 层完全没有利用这一能力。Phase 2 的 approval-flow demo 已经通过手写 raw CozoDB 脚本验证了时间旅行的可行性，但这些代码绑定在 demo 中，无法复用。

演进规划 Phase 3 明确指出三个缺失：属性历史版本（谁在什么时候改了什么）、关系有效期（人员在某时间段属于某部门）、时间点快照查询（去年 Q3 的组织架构）。这些是从"带类型的 property graph"进化到"真正的本体论系统"的关键能力。

## "要做"和"不做" (Goals / Non-Goals)

**目标:**
- 将 `om_property` 和 `om_edge` 改造为 bi-temporal 结构（valid_time + tx_time 双时间轴）
- 在 `OmValueType` 中新增 `Validity` 类型
- 提供属性历史查询 API（`getPropertyHistory`）和时间点快照查询 API（`getPropertyAsOf`、`getEntityViewAsOf`）
- 提供关系历史查询 API（`getEdgeHistory`）和时间点关系查询 API（`getNeighborsAsOf`）
- 确保 Action/Mutation/Constraint 行为层与 temporal 属性协同工作
- 实现旧 schema 到新 bi-temporal schema 的自动迁移
- 重构 approval-flow demo，将 raw CozoDB 时间旅行代码替换为 om temporal API
- 新增组织架构变迁 demo（org-timeline），展示人员调动时间轴和任意时间点组织架构快照

**非目标:**
- 不实现 schema 版本化与迁移框架（Phase 4 范围）
- 不实现本体级访问控制（Phase 4 范围）
- 不修改 CozoDB 引擎或 cozo-dsl.js
- 不实现 materialized temporal view（物化时间视图）
- 不支持 temporal 属性的 eager 计算或缓存优化

## 变更内容（What Changes）

- **BREAKING** `om_property` 关系结构变更：从 `{ entity_id, attr_name => value }` 改为 `{ entity_id, attr_name, valid_time: Validity => value, tx_time: String }`
- **BREAKING** `om_edge` 关系结构变更：从 `{ from_id, rel_name, to_id => props }` 改为 `{ from_id, rel_name, to_id, valid_time: Validity => props, tx_time: String }`
- **BREAKING** `setProperty` 签名变更：新增可选 `options.validTime` 参数
- **BREAKING** `linkEntities` 签名变更：新增可选 `options.validTime` 参数
- `OmValueType` 新增 `'Validity'` 枚举值
- 新增 API：`getPropertyHistory`、`getPropertyAsOf`、`getEntityViewAsOf`、`getNeighborsAsOf`、`getEdgeHistory`
- `initSchema` 新增旧 schema 检测与自动迁移逻辑
- `getProperty` / `getEntityView` 改造为查询最新有效值（latest valid_time）
- ActionContext 中 `getProperty` / `setProperty` 支持 temporal 参数
- 重构 `approval-flow.js` demo，移除 raw CozoDB 时间旅行代码
- 新增 `org-timeline.js` demo

## 影响范围（Impact）

- 受影响的功能规范：cozo-om 核心数据模型（om_property、om_edge）、所有读写 API、行为层集成、viz demo
- 受影响的文件：`cozo-lib-bun/cozo-om.js`、`cozo-lib-bun/cozo-om.d.ts`、`cozo-lib-bun/__tests__/`、`cozo-lib-bun-viz/server/src/demos/approval-flow.js`、`cozo-lib-bun-viz/server/src/demos/org-timeline.js`（新增）、`cozo-lib-bun-viz/e2e/`
