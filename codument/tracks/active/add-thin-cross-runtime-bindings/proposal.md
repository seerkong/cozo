# 变更：新增跨运行时 Cozo 原生薄绑定

## 背景和动机 (Context And Why)

现有绑定混合了运行时封装和更高层能力，无法作为只负责加载 Cozo 原生编译产物的稳定发布基础。本变更提供独立的 npm 和 NuGet 包，令对象建模、本体和业务行为成为可选上层依赖。

## "要做"和"不做" (Goals / Non-Goals)

**目标:**

- 新增 `depa-cozo-bun/`，发布名为 `depa-cozo`，同时支持 Bun 与 Node.js。
- 新增 `depa-cozo-csharp/`，NuGet 包名为 `Depa.Cozo`，支持 .NET Core 运行时。
- 基于已有的 `cozo-node` 与 `cozo_c` 原生边界构建多平台制品。
- 在 `cozo_c` 中提供多语句事务 C ABI，供 `Depa.Cozo` 的事务包装调用。
- 提供不带凭证的本地构建/打包验证，以及手动触发的 CI 发布入口。

**非目标:**

- 不迁移或暴露 OM、ontology、DSL、Action、Mutation 等上层能力。
- 不在本次直接向 npmjs.org 或 nuget.org 推送版本。
- 不承诺尚未构建验证的 RID（如 Windows arm64）。

## 变更内容（What Changes）

- 增加两个独立 package 目录及其 README、类型/托管 API、构建脚本。
- 增加按 `os-arch` / NuGet RID 选择原生制品的加载约定。
- 增加 `cozo_multi_transact`、`cozo_run_tx`、`cozo_commit_tx` 与 `cozo_abort_tx`，并以事务句柄管理其生命周期。
- 增加手动发布 workflow，在各宿主平台构建原生制品后汇总打包。
- 将历史 `cozodb-wrapper` 回归测试保留在薄 JavaScript 包，并验证 `.NET` 的 ProjectReference 与 PackageReference 两种 native asset 装载路径。

## 影响范围（Impact）

- 受影响的能力（behaviors）：`thin-cross-runtime-bindings`。
- 受影响的代码：新增 `depa-cozo-bun/`、`depa-cozo-csharp/` 和 GitHub Actions workflow。
