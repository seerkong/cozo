# 变更：基于 DEPA 的 .NET OM 封装

## 背景和动机 (Context And Why)

当前基于 Cozo 的本体论相关能力主要在 Node/Bun 侧封装，例如 `cozo-lib-bun/cozo-om.js` 与 `cozo-lib-bun-viz`。`.NET` 绑定目前只是 `cozo_c` 的薄包装，缺少与 Node OM 同等的本体建模、治理与规则能力。

本变更在 `cozo-lib-dotnet/src/Om/` 中新增 C# OM 层，并采用 DEPA capsule 组织方式：公共 facade 保持易用，内部按 contract / runtime / input / logic / support 明确分层，避免把 OM 做成一个巨大的 service object。

## "要做"和"不做" (Goals / Non-Goals)

**目标:**
- 在 `cozo-lib-dotnet/src/Om/` 新增 C# OM 封装，作为现有 `cozo-lib-dotnet` NuGet 包的一部分发布。
- 按 DEPA capsule 结构实现：`Contracts/`、`Runtime/`、`Inputs/`、`Logic/`、`Support/`、`Internals/`，并提供薄 facade `CozoOm.cs`。
- 提供与 Node OM 同等的核心本体能力：schema 初始化、类型/属性/关系定义、实体/属性/边写入与读取、类型继承、多态查询、时间语义、schema versioning、权限/约束/计算值、存在规则。
- 以 `ICozoOmStore` 隔离 Cozo IO，使核心 logic 不直接调用 `CozoDb.Run`。
- 为 .NET OM 建立测试基座，以 Node OM 行为作为 parity 参考。
- 将 DEPA 目录结构作为 track 内指导文件保存，见 `guidance/depa-capsule-layout.md`。

**非目标:**
- 不修改 CozoDB 内核、`cozo_c` FFI 或 native binding 语义。
- 不创建独立 NuGet 包；本能力随 `cozo-lib-dotnet` 交付。
- 不扩大存在规则 v1 的 head 形态；C# 版保持 `exists { rel, direction?, toType }`，属性存在性仍由 required 属性与 validation 覆盖。
- 不要求逐行翻译 `cozo-om.js`；目标是行为同等与结构更清晰。
- 不在 `Logic/` 层引入全局单例、隐藏 CozoDb 依赖或 runtime 业务方法。

## 变更内容（What Changes）

- 新增 `cozo-lib-dotnet/src/Om/` DEPA capsule 目录与 C# OM public facade。
- 新增 store effect contract 与 CozoDb support adapter。
- 新增 C# OM model/input/result 类型。
- 分阶段实现 Node OM parity 能力：
  - schema/TBox/ABox 基础能力
  - type hierarchy、attribute/relation inheritance 与 alias resolution
  - temporal property/edge reads and history
  - constraints/computed/actions/mutations/interceptors 的 C# 对应封装
  - schema snapshots/diff/migration/rollback
  - permission/access check
  - existential rules define/list/check/apply
- 新增 .NET tests 覆盖关键 parity case。

## 影响范围（Impact）

- 受影响的能力（behaviors）：`cozo-dotnet-om`
- 受影响的代码：
  - `cozo-lib-dotnet/src/Om/**`
  - `cozo-lib-dotnet/README.md`
  - `cozo-lib-dotnet/example/**`（按需增加 OM 示例）
  - `cozo-lib-dotnet` 测试项目或测试目录（实现阶段确定）
  - 可能的 `cozo-lib-dotnet/Cozo.DotNet.csproj`（仅在测试/包元数据需要时修改；编译 include 当前已覆盖 `src/**/*.cs`）
