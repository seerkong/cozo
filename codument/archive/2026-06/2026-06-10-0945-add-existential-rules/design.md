# 方案设计：存在规则子系统

## 上下文

- cozo-om（`cozo-lib-bun/cozo-om.js`）是基于 CozoDB 的本体建模层，已具备继承/多态、bi-temporal 属性与边、schema 版本化（snapshot/diff/rollback + alias）、Hybrid ABAC 权限
- 存在规则（∀x body(x) → ∃y head(x,y)）超出纯 Datalog 表达力；引擎级 chase 不可行（见 `decisions/existential-rules-v1-scope.md`）
- 复用基础设施清单见 `analysis/findings.md`

## 方案概览

1. **数据模型**
   - 新增存储关系 `om_existential_rule_def { rule_name: String => spec: Json, mode: String, message: String, enabled: Bool }`
   - `initSchema` 沿用 `_runDslCreateIgnoreConflict` 模式幂等建表
   - spec 为纯 JSON（区别于 constraint 层的 JS 回调），可持久化、可进 snapshot、可 diff

2. **规则 spec 形态（v1）**
   - body：`forEach: { type, where?: [{ attr, op, value }] }`，op 支持 `=`/`!=`/`>`/`>=`/`<`/`<=`（对齐权限层 `_evaluateAbacOp` 的能力子集）；where 条目必须显式给出 value（undefined 在 define 时拒绝——JSON 持久化会丢弃 undefined 键，规则将静默永不匹配；null 合法）
   - head：`exists: { rel, direction?: 'out'|'in', toType }`（仅此一种形态，Durable 决策）
   - 物化参数：`materialize?: { labelTemplate?, props? }`
   - `mode: 'check'（默认） | 'materialize'`
   - define 时校验：type/rel 存在性（经 `resolveType`/`resolveRel` canonicalize）、where 字段合法性、head 完整性

3. **违例检测（checkExistentialRules）**
   - 每条规则编译为一个集合语义查询（body 实例集 − head 满足集，`not sat[id]` 在引擎内求差），按 body 类型闭包逐类型名执行一次（与 `findByType`/`aggregateByType` 的既有逐类型模式一致；head 的 toType 闭包与 rel 别名集以参数传入、由 `is_in` 在单查询内判定）：
     - body 实例集：`*om_entity` 中 `type_name` ∈ forEach.type 的后代闭包（复用 `getDescendants`），where 条件 join `om_property` 当前快照
     - head 满足判定：存在 `om_edge`（@NOW 或 asOf，复用 `_getOutgoingNeighborsForPerm` 的时间语义片段）且对端实体 `type_name` ∈ toType 后代闭包
     - 违例 = body 实例集 − head 满足集
   - 返回 `[{ rule, entityId, message }]`；options：`{ rules?, asOf? }`；disabled 规则跳过

4. **Skolem chase（applyExistentialRules）**
   - 宿主 JS 驱动循环（不依赖引擎递归）：
     ```
     for iteration in 1..maxIterations:
       roundCreated = []
       for rule in enabled materialize rules:
         for v in 违例查询(rule):           # 满足性先检：已满足/人工补录不进入
           skolemId = 'skolem:' + sha256(rule.name + '|' + v.entityId).slice(0, 16)
           upsertEntity(skolemId, exists.toType, label)      # 幂等
           setProperty(skolemId, '_skolem_rule', rule.name, { validTime? })
           materialize.props 逐个 setProperty
           linkEntities(v.entityId, exists.rel, skolemId, {}, { validTime? })
           roundCreated.push(...)
       if roundCreated 为空: return { created, iterations, reachedFixpoint: true }
     return { created, iterations: maxIterations, reachedFixpoint: false, diagnostics }
     ```
   - 幂等性来源：Skolem ID 确定性 + `upsertEntity` upsert 语义 + 满足性先检（重跑时违例集为空）
   - 终止防护：仅 maxIterations（默认 10），未收敛返回诊断（哪些规则仍有违例）——P2 决策

5. **治理集成**
   - `_readSchemaSnapshotParts` 增加 `om_existential_rule_def` 读取；`rollbackSchema` 的 `_replaceStoredRelation` 恢复路径覆盖该表；`_diffSchemaSnapshots` 自然获得规则 diff
   - 规则执行时 type/rel/attr 引用统一经 `resolveType`/`resolveRel`/`resolveAttr`，重命名后规则继续工作（同 OM-022 策略稳定性机制）
   - chase 与检测为系统级操作，不经 `checkAccess`

6. **viz 演示（/governance「完整性检查」子 tab）**
   - server（`cozo-lib-bun-viz/server/src/index.js`）新增端点：`GET /api/governance/integrity/rules`、`POST /api/governance/integrity/check`、`POST /api/governance/integrity/apply`，复用既有 governance demo 的 runner/seed 机制
   - 前端 `GovernanceDemo.vue` 新增子 tab：规则列表、违例表格、一键物化按钮、created 结果展示；不触碰既有两个子 tab 的逻辑
   - E2E 使用 `.pw.ts` 后缀（避免 bun test 误执行）

## 影响范围与修改点（Impact）

- 受影响的文件/模块：
  - `cozo-lib-bun/cozo-om.js`（新增 section + initSchema + snapshot parts）
  - `cozo-lib-bun/cozo-om.d.ts`
  - `cozo-lib-bun/__tests__/om-existential-*.test.js`（新增）
  - `cozo-lib-bun-viz/server/src/index.js`、`frontend/src/pages/GovernanceDemo.vue`、`frontend/src/lib/api.ts`
  - `cozo-lib-bun-viz/e2e/governance-integrity.pw.ts`（新增）

## 决策摘要

- 详见 `codument/tracks/add-existential-rules/decisions.md`
- 当前关键结论：head 仅 `exists:{rel,toType}`；viz 放 governance 子 tab；Skolem 标记用 `_skolem_rule` 属性；终止仅 maxIterations 兜底；track id 为 add-existential-rules

## 风险 / 权衡

- **循环规则不收敛** → maxIterations 兜底 + reachedFixpoint:false 诊断；文档说明规则设计应保证无环
- **大实例集上违例查询性能** → 集合语义查询（而非逐实体回调），head 满足判定在引擎内完成；type 闭包在查询前用 JS 计算（避免引擎内递归继承解析），toType 闭包/rel 别名集以参数传入单查询，body 闭包逐类型名执行——查询次数与类型闭包大小（schema 规模）成正比，与实例数无关
- **Skolem ID 截断哈希碰撞** → sha256 截 16 hex（64 bit），按规则名+实体 ID 域内碰撞概率可忽略；ID 前缀 `skolem:` 隔离命名空间
- **snapshot 体积增长** → 规则定义为小 JSON，量级远小于既有 schema parts
- **viz 改动波及既有 governance tab** → 子 tab 组件隔离 + E2E 回归既有 tab 行为

## 兼容性设计

- 未定义任何规则时：无新查询路径被触发，既有 API 行为不变（OM-027 backward-compat case 验证）
- `om_existential_rule_def` 缺失（旧库升级前）：initSchema 自动建表；读取路径容忍空表
- 旧版本 snapshot（无规则 part）回滚：规则 part 缺失视为空集，不报错

## 待解决问题

- （无——P0/P1/P2 决策均已确认）
