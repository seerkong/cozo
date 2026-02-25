# 变更：cozo-om 存在规则（Existential Rules）子系统

## 背景和动机 (Context And Why)

cozo-om 已具备类型继承、行为层、bi-temporal、schema 版本化与权限集成，但缺少表达「每个 X 必须存在 Y」这类**存在约束**（existential rules / TGD，Nemo 等规则引擎的核心能力）的手段。主数据治理场景大量需要此类约束：每个已确认订单必须有发货单、每个部门必须有负责人等。

直接改 CozoDB 内核实现 chase 算法是研究级改造且会形成永久 fork。本变更采用应用层方案：**Skolem 改写 + 宿主驱动循环 + 违例检测查询**三件套，在 cozo-om 层覆盖存在规则的绝大部分实际价值。

## "要做"和"不做" (Goals / Non-Goals)

**目标:**
- 声明式存在规则定义与持久化（`om_existential_rule_def` 表 + `defineExistentialRule` / `listExistentialRules`）
- 集合语义违例检测（`checkExistentialRules`，支持多态类型匹配、where 属性条件、@NOW 与 asOf 时间语义）
- Skolem chase 物化（`applyExistentialRules`：确定性 Skolem ID、幂等重跑、满足性先检、maxIterations 终止兜底、`_skolem_rule` 来源标记、validTime 透传）
- 与 schema 版本化集成：规则定义纳入 snapshot/diff/rollback；规则引用经 alias 解析对重命名免疫
- viz `/governance` 页新增「完整性检查」子 tab 演示（违例列表 + 一键物化）

**非目标:**
- 不修改 CozoDB 内核（cozo-core 查询求值器）
- v1 head 形态仅支持「∃ 关系边 + 目标实体」；属性存在性由 `required` + `validateConstraints` 覆盖，不重复建设
- 不做静态规则无环预检（v1 仅 maxIterations 兜底）
- 不做完整 restricted chase 语义；不做 Nemo 导出口子（留待后续）
- 不修改现有 constraint 层（`defineConstraint` / `validateConstraints`）的语义与 API
- 不按 subject 权限过滤违例报告（chase 与检测为系统级操作）

## 变更内容（What Changes）

- `cozo-lib-bun/cozo-om.js`：新增存在规则 section（建表、define/list/check/apply 四个 API、违例查询编译、chase 驱动循环）；`initSchema` 增加 `om_existential_rule_def` 幂等建表；`_readSchemaSnapshotParts` 纳入规则表
- `cozo-lib-bun/cozo-om.d.ts`：新增对应类型定义
- `cozo-lib-bun/__tests__/`：新增 `om-existential-*.test.js` 单元测试
- `cozo-lib-bun-viz/server/src/index.js`：新增 integrity 相关 API 端点
- `cozo-lib-bun-viz/frontend/src/pages/GovernanceDemo.vue`：新增「完整性检查」子 tab
- `cozo-lib-bun-viz/e2e/`：新增 governance integrity E2E（`.pw.ts`）
- 无 BREAKING 变更：未定义规则时既有 API 行为完全不变

## 影响范围（Impact）

- 受影响的功能规范：
  - `cozo-om`（新增 OM-024 ~ OM-027）
  - `cozo-lib-bun-viz`（新增 VIZ-007）
