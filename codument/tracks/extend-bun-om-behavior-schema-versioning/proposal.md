# 变更：扩展 Bun OM Behavior Schema Versioning

## 背景和动机 (Context And Why)

Bun OM 已将 constraint、computed、action、mutation、interceptor 五类 behavior definition 和 `om_behavior_binding` 持久化，并通过 runtime-instance callback registry 推导 `unbound | unresolved | ready`。但是现有 schema snapshot、diff、migration snapshot 和 rollback 只覆盖类型、属性、关系、alias、permission 与 existential rule，尚未覆盖 behavior Data。

这造成三个事实缺口：

- schema snapshot 不能完整描述某版本的本体行为元数据和 callback binding identity；
- schema diff 不能报告 behavior definition 或 binding 的增加、删除和更新；
- rollback 只恢复其他 schema 关系，behavior 会停留在当前版本，无法形成一致的历史 schema 状态。

历史 snapshot 还存在一个不可消除的信息缺失：旧格式没有 `behavior` section，字段缺失只能表示 `unknown`，不能自动解释为“当时为空”或“保留当前值”。已冻结的兼容决策要求默认拒绝，并只在调用者显式选择 `preserve` 或 `clear` 后产生效果。

## "要做"和"不做" (Goals / Non-Goals)

**目标:**

- 让所有新写入的 Bun schema snapshot 都显式包含 `behavior` section；即使六张关系均为空也不得省略。
- snapshot 覆盖五类 behavior definition 和 `om_behavior_binding`，并保持确定性关系顺序与行顺序。
- 为六张关系提供 deterministic keyed diff，明确区分 section 缺失与显式空集合。
- 让 `applySchemaMigration` 自动生成的来源版本和目标版本 snapshot 使用同一 behavior-aware 格式。
- 在一个持久事务内原子恢复 behavior definition、binding identity、其他 schema facts 和 schema state。
- 对 legacy snapshot 缺少 `behavior` section 的 rollback 默认返回结构化、无副作用拒绝；支持显式 `preserve | clear`。
- rollback 与同一 runtime 的 import、catalog、execution scope capture 和 contended clear 共用 behavior gate，只允许观察完整的 rollback 前态或后态。
- rollback 后保留当前 runtime callback registry，并根据恢复后的 binding identity 重新推导 readiness。

**非目标:**

- 不修改 `cozo-lib-dotnet` 或重新设计 .NET schema versioning。
- 不持久化 JavaScript function、closure、脚本源码、runtime callback registration 或 readiness。
- 不改变 behavior manifest V1 wire contract、binding identity 规则或现有 callback 执行语义。
- 不回填或原地重写已有 legacy snapshot；历史缺失事实保持 `unknown`。
- 不扩展 migration step DSL 来创建、修改或删除 behavior；本 track 只保证 migration 生成的 snapshot 捕获事务内的当前 behavior facts。
- 不解决不同 runtime instance 共享同一数据库时的进程级全局锁；一致性边界仍是 runtime-instance gate 加 Cozo 持久事务。

## 变更内容（What Changes）

- 新增显式 `behavior` snapshot section，包含 `formatVersion: 1` 和六个具名关系数组。
- 扩展 snapshot reader/composer、migration snapshot materialization、checksum 输入和 TypeScript public types。
- 扩展 schema diff，按每张关系的持久主键输出稳定排序的 `added`、`removed`、`updated`。
- legacy/new snapshot 混合 diff 显式报告 section presence 和不可比较诊断，不把未知伪装为空集合。
- 扩展 `rollbackSchema` options，增加 `legacyBehaviorPolicy?: "preserve" | "clear"`。
- 扩展 rollback result，增加结构化 compatibility diagnostics 和实际采用的 behavior policy。
- **BREAKING:** 实际回滚到缺少 `behavior` section 的 legacy snapshot 时，不再静默继续；默认返回 `ok: false` 且零副作用，调用方必须显式选择兼容策略。
- 增加 snapshot、diff、migration、rollback、故障原子性、readiness 重算和 gate 并发测试。

## 影响范围（Impact）

- 受影响的能力（behaviors）：`cozo-om`
- 受影响的代码：`cozo-lib-bun/cozo-om.js`、`cozo-lib-bun/cozo-om.d.ts`
- 受影响的测试：Bun OM schema snapshot/diff/migration/rollback 测试，以及新增 behavior schema versioning focused tests
- 持久数据影响：只改变未来写入的 `om_schema_snapshot.snapshot_json` 形状；不新增 Cozo stored relation，不自动改写历史 snapshot

