# 变更：为 Bun OM 增加跨运行时行为可移植性

## 背景和动机

.NET OM 已经把行为定义、callback binding identity、runtime callback 与 readiness 分为不同事实域，并提供 V1 canonical JSON manifest、宽松/严格导入和 fail-closed 执行。Bun OM 目前只有持久化定义与进程内 JavaScript callback，缺少 binding identity、catalog、manifest、restart readiness 和 unresolved 诊断。

本 track 反向增强 Bun，使其采用与 .NET 相同的 V1 wire contract。JavaScript 函数体继续只存在于 runtime registry，不能进入 Cozo schema 或 manifest。

## 目标

- 增加 `om_behavior_binding` 持久 relation 和五类 behavior、六种 callback slot 的稳定 identity。
- 增加 behavior catalog、canonical JSON encode/decode/export 和 import API。
- readiness 由持久 binding identity 与当前 runtime registry 实时派生。
- 增加 metadata-only import、显式 register/rebind 和 `custom` validator。
- 所有行为执行入口对 bound-but-unresolved callback 返回结构化 `OMR1001`，不得回退或部分执行。
- `requireReady` 导入在 callback 不完整时零副作用拒绝。
- 持久 metadata/binding 与 runtime registry publication 保持原子或可补偿。
- 与 .NET V1 golden fixture、诊断和场景测试对齐。

## 非目标

- 不把 behavior definition 或 binding 纳入 Bun schema snapshot、diff 或 rollback；该范围属于 G13。
- 不持久化 JavaScript 函数、闭包、脚本源码或 readiness。
- 不改变现有未绑定 `define*` API 的原生执行语义。
- 不引入脚本引擎或远程 callback 分发。

## 影响

- `initSchema` additive 创建 `om_behavior_binding`。
- 显式 runtime 可以在同一数据库上拥有不同 callback readiness。
- 已持久化 binding 但当前 runtime 未注册同 identity callback 时，执行将 fail closed。
- legacy 未绑定行为保持 `unbound` 且继续可执行。

## 验收

- Bun 生成的 canonical JSON 与 .NET V1 golden bytes 一致。
- 五类 behavior、六种 slot 均覆盖 `unbound -> unresolved -> ready -> clear -> unresolved -> rebind -> ready`。
- custom validator、父子 action、mutation、computed 和 inherited interceptor 均遵守 readiness。
- permissive import、strict import、故障补偿和多 runtime 隔离测试通过。
- Bun OM 与完整 Bun 测试无回归。
