## 上下文

cozo-om 当前有 46 个导出函数，核心数据模型为 10+ 张存储关系。Phase 1 实现了类型继承与多态，Phase 2 实现了 Action/Mutation/Interceptor/Constraint/Computed 行为层。

本次变更需要将 `om_property` 和 `om_edge` 从无时间感知改造为 bi-temporal 结构，同时保持行为层（Action/Constraint/Computed）的协同工作。

CozoDB 引擎约束：
- `Validity` 类型必须是存储关系的最后一个 key 列（`cozo-core/src/storage/mod.rs:123`）
- 每个关系只能有一个 `Validity` 列
- `@ timestamp` 查询仅对含 `Validity` 列的关系生效
- `:replace` 会销毁旧关系并重建，所有数据丢失（`cozo-core/src/query/stored.rs:59-119`）
- Validity 支持 ASSERT（记录存在）和 RETRACT（记录删除）语义
- Validity 值接受：`"ASSERT"`/`"RETRACT"`（当前时间）、RFC 3339 字符串、`"~RFC3339"`（RETRACT）、`[microseconds, bool]` 数组

## 方案概览

1. **Bi-temporal 模型：valid_time (Validity) + tx_time (String)**
   - `valid_time` 使用 CozoDB 原生 `Validity` 类型作为最后一个 key 列，获得原生 `@ timestamp` 时间旅行查询能力
   - `tx_time` 使用 String 类型（UTC RFC 3339 格式）存储在 value 列，作为系统审计时间戳
   - 这是"valid-time-first + tx audit stamp"模型，非完全双轴原生查询，但覆盖主要业务场景

2. **om_property 改造**
   - 旧结构：`om_property { entity_id: String, attr_name: String => value: String }`
   - 新结构：`om_property { entity_id: String, attr_name: String, valid_time: Validity => value: String, tx_time: String }`
   - `valid_time` 为最后一个 key 列，启用 CozoDB 原生时间旅行
   - 同一 `(entity_id, attr_name)` 可有多个时间版本共存
   - 读取语义：
     - `getProperty(runner, id, attr)` → 使用 `@ "NOW"` 查询当前有效值
     - `getPropertyAsOf(runner, id, attr, ts)` → 使用 `@ $ts` 查询指定时间点
     - `getPropertyHistory(runner, id, attr)` → 不带 `@` 查询所有原始版本

3. **om_edge 改造**
   - 旧结构：`om_edge { from_id: String, rel_name: String, to_id: String => props: String }`
   - 新结构：`om_edge { from_id: String, rel_name: String, to_id: String, valid_time: Validity => props: String, tx_time: String }`
   - 部门调动建模方式：
     - ASSERT `emp → dept-A` at Jan
     - RETRACT `emp → dept-A` at Jul（使用 `~RFC3339` 格式）
     - ASSERT `emp → dept-B` at Jul
   - `getNeighborsAsOf` 利用 `@ timestamp` 自动过滤 RETRACT 记录

4. **OmValueType 扩展**
   - 新增 `'Validity'` 到 `OmValueType` 枚举
   - `defineAttribute` 支持 `value_type: 'Validity'`
   - `setProperty` 对 Validity 类型属性接受 RFC 3339 字符串

5. **API 变更**
   - `setProperty(runner, entityId, attrName, value, options?)` — options 新增 `validTime?: string`
     - 不传 validTime 时使用 `"ASSERT"`（CozoDB 自动取当前时间）
     - 传 validTime 时使用 RFC 3339 字符串
   - `linkEntities(runner, fromId, relName, toId, options?)` — options 新增 `validTime?: string`
   - `unlinkEntities(runner, fromId, relName, toId, options?)` — 新增 API，写入 RETRACT 记录
   - `getProperty` 内部改为 `@ "NOW"` 查询
   - `getEntityView` 内部改为 `@ "NOW"` 查询所有属性
   - 新增 temporal 查询 API：
     - `getPropertyHistory(runner, entityId, attrName, options?)`
     - `getPropertyAsOf(runner, entityId, attrName, timestamp)`
     - `getEntityViewAsOf(runner, entityId, timestamp)`
     - `getNeighborsAsOf(runner, entityId, relName, timestamp)`
     - `getEdgeHistory(runner, fromId, relName, toId?, options?)`

6. **Computed 属性与 temporal 的交互**
   - `getProperty` 保持 stored-first → computed fallback 策略
   - `getPropertyAsOf` 同样支持 computed fallback，但需将 asOf 上下文传递给 compute 函数
   - 对严格审计快照场景，temporal 查询 API 支持 `{ includeComputed: false }` 选项
   - 文档明确：computed 函数可能随代码版本变化，历史快照中的 computed 值反映当前代码逻辑而非历史代码逻辑

7. **ActionContext temporal 集成**
   - `ctx.setProperty(attrName, value, options?)` 支持 `validTime`
   - `ctx.getProperty(attrName)` 默认返回当前有效值
   - 约束校验基于 `@ "NOW"` 的最新有效值执行

8. **Schema 迁移策略**
   - 采用 read-rebuild-reinsert 方案（方案 B）：
     1. `initSchema` 检测旧 schema（查询 `om_property` 的列结构）
     2. 读取旧 `om_property` 和 `om_edge` 的所有数据到内存
     3. 使用 `:replace` 重建关系（新 schema）
     4. 将旧数据以当前时间作为 valid_time 和 tx_time 重新插入
     5. 验证行数一致
   - 迁移幂等：如果已是新 schema 则跳过
   - 迁移在 `initSchema` 中自动执行，对用户透明

9. **Viz Demo 更新**
   - 重构 `approval-flow.js`：
     - 移除 `approval_request_ledger` 手写关系和 raw CozoDB 脚本
     - 使用 `setProperty` + `validTime` 写入审批状态
     - 使用 `getPropertyHistory` / `getPropertyAsOf` 查询时间轴
     - 时间轴 sheet 改为调用 om temporal API
   - 新增 `org-timeline.js`：
     - 类型：Department、Employee、Team
     - 关系：belongs_to（Employee → Department）、manages（Employee → Team）
     - 种子数据：多次人员调动历史
     - 查询：任意时间点组织架构快照、人员调动时间轴、部门人数变化

## 影响范围与修改点（Impact）

| 文件 | 变更类型 | 说明 |
|---|---|---|
| `cozo-lib-bun/cozo-om.js` | 修改 | 核心变更：om_property/om_edge schema 改造、所有读写 API temporal 化、迁移逻辑、新增 temporal 查询 API、unlinkEntities |
| `cozo-lib-bun/cozo-om.d.ts` | 修改 | TypeScript 类型声明：OmValueType 新增 Validity、新 API 签名、options 类型更新 |
| `cozo-lib-bun/__tests__/*.test.js` | 新增/修改 | temporal 属性/关系/迁移/查询测试；现有测试适配新 schema |
| `cozo-lib-bun-viz/server/src/demos/approval-flow.js` | 修改 | 移除 raw CozoDB 时间旅行代码，改用 om temporal API |
| `cozo-lib-bun-viz/server/src/demos/org-timeline.js` | 新增 | 组织架构变迁 demo |
| `cozo-lib-bun-viz/server/src/demos/index.js` | 修改 | 注册 org-timeline demo |
| `cozo-lib-bun-viz/e2e/smoke.spec.ts` | 修改 | 新增 org-timeline E2E 测试 |

## 决策

- **决策：使用 Validity 作为 valid_time，String 作为 tx_time**
  - 原因：CozoDB 仅支持一个 Validity 列，valid_time 是主要查询维度；tx_time 作为审计元数据不需要原生时间旅行查询
  - 替代方案：两个独立关系分别存储 valid_time 和 tx_time → 复杂度翻倍，查询需要 join
  - 权衡：tx_time 不支持 `@ timestamp` 原生查询，需要手动过滤

- **决策：直接改造 om_property/om_edge 而非新增独立历史关系**
  - 原因：用户选择允许小幅 breaking change；单一关系避免数据同步问题
  - 替代方案：新增 om_property_history 保持 om_property 不变 → 需要双写和同步逻辑
  - 权衡：所有现有读写代码需要适配 `@ "NOW"` 语义

- **决策：getProperty 默认使用 `@ "NOW"` 语义**
  - 原因：保持"读取当前有效值"的直觉行为；`@ "NOW"` 是 CozoDB 原生支持的
  - 替代方案：使用 `@ "END"` 获取最新录入值 → 语义不直觉，可能返回未来生效的值

- **决策：关系终止使用 RETRACT 语义而非 end_date 列**
  - 原因：CozoDB Validity 原生支持 RETRACT（`~RFC3339` 格式），`@ timestamp` 查询自动过滤已 RETRACT 的记录
  - 替代方案：增加 end_date 列手动管理有效期 → 查询需要手动过滤，无法利用原生时间旅行
  - 权衡：RETRACT 语义对用户不够直觉，需要 `unlinkEntities` API 封装

- **决策：迁移采用 read-rebuild-reinsert 方案**
  - 原因：CozoDB `:replace` 会销毁数据，必须先读出再重建；方案简单可靠
  - 替代方案：不迁移，要求用户重新初始化 → 对已有数据的用户不友好
  - 风险：大数据量迁移可能耗时 → 缓解：cozo-om 当前为嵌入式场景，数据量有限

## 风险 / 权衡

- **Breaking change 风险：om_property/om_edge schema 变更** → 缓解：initSchema 自动迁移；文档明确升级步骤
- **性能风险：getProperty 每次使用 `@ "NOW"` 查询** → 缓解：CozoDB 的 Validity skip-scan 算法高效（`cozo-core/src/data/tuple.rs:60-84`），单版本场景几乎无额外开销
- **同一 (entity_id, attr_name, valid_time) 覆写风险：两次写入相同 valid_time 会覆盖** → 缓解：tx_time 记录实际写入时间，可审计；文档说明 valid_time 精度建议
- **Computed 属性历史不一致风险：computed 函数反映当前代码而非历史代码** → 缓解：文档明确；提供 `includeComputed: false` 选项
- **现有测试适配工作量** → 缓解：大部分测试不涉及 temporal，仅需确保 `@ "NOW"` 默认行为与旧行为一致

## 待解决问题

- `ingestBatch` 是否需要支持批量 temporal 写入（每条记录指定不同 validTime）？建议 v1 先不支持，后续按需扩展
- 是否需要 `getPropertyLatestRecorded`（使用 `@ "END"` 查询最新录入值，含未来生效）？建议 v1 先不暴露，观察需求
- org-timeline demo 的具体组织架构数据设计（部门数量、人员数量、调动次数）待实现时确定
