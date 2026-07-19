# 变更：统一 OM Runtime Registry 生命周期

## 背景和动机 (Context And Why)

Bun OM 当前把 action、mutation、interceptor、constraint 与 computed callback 存在五个进程全局 Map 中，任一数据库调用 `clearRegistry()` 都会清除所有数据库的 callback。`.NET` OM 已经为每个 `CozoOm` 实例创建独立 registry，并以 immutable snapshot 和 behavior gate 保证导入与执行的一致性，但没有公共的实例级 clear/reset API。

本 track 统一两侧的生命周期语义：registry 的 owner 是一个 OM runtime instance，clear 只作用于该实例，且不删除持久化 behavior definition 或 callback binding identity。

## 目标

- Bun 新增显式 runtime carrier，并让现有 OM 自由函数可接受 runtime 或 legacy runner。
- Bun callback registry 按 runtime 实例隔离；legacy 自由函数委托给一个显式 legacy runtime adapter。
- Bun `clearRegistry(runtime?)` 清除指定 runtime；无参调用仅清除 legacy runtime。
- `.NET` 新增 gate-aware、实例级 `ClearRegistryAsync`。
- 已开始并捕获 registry snapshot 的行为执行不被并发 clear 撕裂。
- 两侧增加配对测试，证明实例隔离、clear、持久事实保留和兼容入口。

## 非目标

- 本 track 不实现 Bun behavior manifest、binding relation 或 schema snapshot 扩展。
- 不改变 behavior 继承、interceptor 顺序、事务或 rollback 语义。
- 不把 C# registry 改为进程全局。
- 不序列化 JavaScript 函数或 C# delegate。

## 影响

- Bun 增加新的推荐 runtime-instance 入口；现有传 runner 的调用保持兼容。
- Bun 的无参 `clearRegistry()` 从“清所有调用方共享状态”明确为“清 legacy runtime 状态”。
- `.NET` 增加异步清空入口，以便通过现有 behavior gate 与 import/execution 串行化。
- 变更仅影响进程内 callback 状态，不改变 Cozo schema。

## 验收

- 两个 Bun runtime 可定义相同 behavior key 的不同 callback，互不泄漏。
- 清除一个 Bun runtime 后，另一个 runtime 仍可执行；legacy 入口仍通过旧测试。
- `.NET` 清除一个 `CozoOm` 后，另一个 `CozoOm` registry 不受影响。
- clear 后持久化 metadata/binding identity 不变，catalog readiness 如实降级。
- Bun 与 .NET focused tests 和完整 OM tests 通过。
