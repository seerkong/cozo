# Design

## 上下文与不变量

本设计延续已经建立的事实边界：

```text
Persisted schema Data:
  five behavior definition relations + om_behavior_binding

Process-local runtime Data:
  JavaScript callbacks + runtime binding identity registrations

Derived Processor output:
  catalog readiness = persisted binding identity x current runtime registry
```

snapshot、diff、migration 和 rollback 只处理 persisted schema Data。callback function、script source 和 readiness 永远不进入 snapshot。

## Behavior Snapshot V1

所有新生成的 snapshot 根对象必须有一个具名 `behavior` section：

```json
{
  "behavior": {
    "formatVersion": 1,
    "om_constraint_def": [],
    "om_computed_def": [],
    "om_action_def": [],
    "om_mutation_def": [],
    "om_interceptor_def": [],
    "om_behavior_binding": []
  }
}
```

空 section 仍完整写出六个空数组。`behavior` 是这些事实的唯一 snapshot 表示，不在 `schema` 下复制第二份权威数据。

关系列和 key 固定如下：

| Relation | Row columns | Key columns |
|---|---|---|
| `om_constraint_def` | `type_name, constraint_name, constraint_type, message` | `type_name, constraint_name` |
| `om_computed_def` | `type_name, attr_name, description` | `type_name, attr_name` |
| `om_action_def` | `type_name, action_name, description` | `type_name, action_name` |
| `om_mutation_def` | `type_name, mutation_name, description` | `type_name, mutation_name` |
| `om_interceptor_def` | `type_name, action_name, phase, seq, description` | `type_name, action_name, phase, seq` |
| `om_behavior_binding` | `behavior_kind, owner_type, behavior_name, callback_slot, phase, seq, binding_id` | `behavior_kind, owner_type, behavior_name, callback_slot, phase, seq` |

snapshot relation order使用上表顺序；每张表按 key tuple 使用 `_ordinalCompare` 语义稳定排序，数字 `seq` 按数值比较。不得依赖进程 locale。

## Deterministic Keyed Diff

`SchemaDiff.behavior` 使用如下投影：

```text
fromPresence: present | missing
toPresence: present | missing
comparable: boolean
diagnostics: SchemaVersioningDiagnostic[]
relations:
  <relation>: { added, removed, updated }
```

当两侧都存在 `behavior` section 时：

- 六张表全部出现在 `relations` 中，即使没有差异；
- `added`、`removed` 按 key tuple 排序；
- `updated` 形如 `{ key, from, to }`，按 key tuple 排序；
- metadata 非 key 列改变属于 `updated`；
- binding identity 改变属于 `om_behavior_binding.updated`；
- 重复 key 或错误行形状不得静默覆盖，应产生稳定结构诊断。

当任一侧缺少 section 时，`comparable=false`，返回 `OMSV1001` 诊断并保持 `relations` 为空；不得把缺失侧合成为六个空数组并伪造 additions/removals。这个规则只约束 diff 的诚实表达，不会修改 snapshot。

## Migration Snapshot

`writeSchemaSnapshot` 和 `_composeSchemaSnapshot` 使用统一 behavior reader。`applySchemaMigration` 的两个自动 snapshot 路径也必须复用它：

1. 来源版本没有 snapshot 时，在 migration write transaction 内捕获迁移前 behavior Data；
2. 执行 migration steps；
3. 在同一 transaction 内捕获目标版本 behavior Data；
4. 目标 snapshot、version checksum、schema state 和 migration log 一起提交。

已存在的 legacy 来源 snapshot 不做隐式升级。新创建的来源/目标 snapshot 即使 behavior 为空，也有显式 section。

## Legacy Rollback Policy

`RollbackSchemaOptions` 增加：

```ts
legacyBehaviorPolicy?: "preserve" | "clear";
```

行为矩阵：

| Target snapshot | Policy | Behavior persistent effect |
|---|---|---|
| section present | omitted / any | 严格恢复 snapshot 中六张关系；显式空 section 会清空 |
| section missing | omitted | 返回 `ok:false`、`OMSV1001`，所有 persistent/runtime effects 为零 |
| section missing | `preserve` | 保留当前六张关系，只回滚其他 schema facts |
| section missing | `clear` | 在 rollback transaction 中把六张关系替换为空 |

`clear` 只清持久 behavior definition 和 binding relation，不清 runtime callback registry。`preserve` 和 `clear` 都要在结果中显式报告实际采用的 policy。

兼容拒绝结果采用 additive public shape：

```ts
interface SchemaVersioningDiagnostic {
  code: "OMSV1001" | string;
  path: "$.behavior";
  message: string;
  allowedPolicies?: readonly ("preserve" | "clear")[];
}

interface RollbackSchemaResult {
  ok: boolean;
  diagnostics: RollbackSchemaDiagnostic[];
  compatibilityDiagnostics: SchemaVersioningDiagnostic[];
  behaviorPolicyApplied: "snapshot" | "preserve" | "clear" | null;
}
```

`OMSV1001` 的含义固定为：目标 snapshot 缺少 behavior section，历史 behavior facts 未知。默认拒绝不得抛出非结构化异常来替代该结果。

## Atomic Rollback

一次有效 rollback 的顺序是：

1. 进入 runner 所属 runtime-instance behavior gate；
2. 读取 current state 和目标 snapshot，完成 legacy policy preflight；
3. 默认拒绝在任何 write transaction、cache invalidation 或 registry publication 前返回；
4. 在一个 Cozo write transaction 中替换目标 schema relations；
5. 对 section-present 恢复六张 behavior relations；对 `preserve` 跳过六张表；对 `clear` 替换为空；
6. 执行既有 entity compatibility validation；
7. 从“目标 snapshot + policy 解析后的有效 behavior section”计算 current checksum；
8. 更新 schema state 并提交；
9. 成功后失效必要的持久读取 cache，释放 gate。

任意 relation replace、validation、checksum/state write 或 commit 失败，都必须由 transaction 保证 schema facts、六张 behavior relations和 schema state 回到 rollback 前状态。rollback 不发布或补偿 runtime registry，因为 registry 从未被修改。

对于 `preserve`，checksum 使用当前被保留的 behavior Data；对于 `clear`，checksum 使用显式空 behavior section。历史 snapshot 原文不被重写。

## Runtime Gate 与 Readiness

rollback 使用 G12 已有的同一个 runtime owner gate：

```text
import
rollback
catalog
execution behavior-scope capture
contended clearRegistry(runtime)
```

gate 持有到 persistent commit 完成，保证新 catalog/execution/clear 只能观察 rollback 前或 rollback 后的完整状态。已经捕获 immutable resolution scope 的 in-flight command 按既有规则完成；新的 observation 必须等待 rollback。

rollback 不执行 callback，也不在 gate 内调用用户代码。成功后：

- runtime registry snapshot 保持引用不变；
- catalog 从恢复后的 definition/binding 与当前 runtime registry 重新投影 readiness；
- exact binding 仍为 `ready`，缺失或不匹配为 `unresolved`，无 binding 为 `unbound`；
- 被恢复为不存在的 behavior 不再出现在 catalog；
- 同数据库的另一 runtime 仍使用自己的 registry，因此可得到不同 readiness。

## Public API 与兼容性

- `SchemaSnapshot.behavior` 对新 snapshot 必有，但 TypeScript 类型保留 optional 读取语义以容纳 legacy payload。
- `SchemaDiff` 增加 typed `behavior` projection，不移除既有字段。
- `RollbackSchemaOptions` 和 result 只做 additive 扩展。
- `rollbackSchema(db, ...)` 继续走 legacy runtime adapter gate；`rollbackSchema(runtime, ...)` 走该 runtime 的独立 gate。
- 同版本 no-op rollback 不执行恢复，因此维持既有成功语义；D8 只约束实际读取并应用历史 snapshot 的 rollback。

## TDD 与验证策略

每个 phase 串行执行 RED → GREEN → REFACTOR：

- snapshot fixtures 覆盖五类 definition、六种 callback slot、空 section、Unicode/key 排序和源码缺席；
- diff fixtures 覆盖 add/remove/update、binding identity update、稳定顺序和 legacy unknown；
- migration fixtures 同时检查自动来源与目标 snapshot；
- rollback fixtures 覆盖 section-present、显式空、legacy reject/preserve/clear、checksum 和失败注入；
- gate fixtures 阻塞 rollback transaction，并检查 catalog/execution/clear 的 pre/post observation；
- readiness fixtures使用两个 runtime 共享数据库，证明 registry 不恢复且 readiness 重算。

最终运行 focused tests、Bun OM tests、Bun full suite、JavaScript syntax check、targeted diff check 和 Codument strict validation。

## 风险 / 权衡

- `preserve` 会产生“目标版本的其他 schema + 当前 behavior”的策略化状态。通过 policy result 和 effective checksum 明示，而不是伪装成目标 snapshot 原样。
- legacy/new diff 无法生成真实 row changes。设计选择返回不可比较诊断，保留 unknown 语义。
- runtime-instance gate 不能阻止另一个 runtime 同时写共享数据库。当前保持已冻结的一致性边界，依赖 Cozo transaction 处理持久原子性。
- 现有 schema diff helper 使用 locale-sensitive 排序。behavior diff 必须采用 ordinal key ordering；不得顺手改变其他 diff section 的公开排序，除非 characterization tests 证明兼容。

## 待解决问题

无。D7 已冻结“只持久化 definition 与 binding identity”，D8 已选择默认拒绝并要求显式 `preserve | clear`。

