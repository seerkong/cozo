# 设计：跨运行时薄绑定

## 边界

`depa-cozo-bun` 只将 JavaScript 调用转换为 `cozo-node` 的 N-API 调用；`depa-cozo-csharp` 只将托管调用转换为 `cozo_c` C ABI 调用。两个包均不得依赖或重新导出对象建模、本体、规则或业务语义代码。

## npm

一个 npm 包名 `depa-cozo` 服务两个 JavaScript runtime。包内的 N-API 二进制按 `<platform>-<arch>` 放在 `native/` 下，loader 使用 `process.platform` 与 `process.arch` 定位。Bun 通过其 Node-API 兼容层加载同一类 `.node` 二进制；Node.js 与 Bun 保留独立的 CI 验证命令。

首批发布 RID 是 `darwin-arm64`、`darwin-x64`、`linux-x64` 和 `win32-x64`。Linux musl 和 ARM64、Windows ARM64 在有对应原生构建 runner 与验证后再作为附加制品发布，避免将未验证的二进制支持写成承诺。

## NuGet

`Depa.Cozo` 定位为 `netstandard2.0` 薄 API，因此可供现代 .NET Core 消费。原生库使用标准 NuGet 布局 `runtimes/<RID>/native/`；.NET restore/publish 基于 RID 自动选择正确的 `.dylib`、`.so` 或 `.dll`。托管层不自行猜测平台路径，也不把二进制复制到源码。

### 事务 C ABI

`cozo_c` 为每个多语句事务分配独立的 `int32_t` 句柄。`cozo_multi_transact(int32_t db_id, bool write, int32_t *tx_id)` 成功时返回空指针，失败时返回需由 `cozo_free_str` 释放的错误字符串；`cozo_run_tx(int32_t tx_id, const char *script, const char *params)`、`cozo_commit_tx(int32_t tx_id)` 和 `cozo_abort_tx(int32_t tx_id)` 返回与既有查询 ABI 一致的 JSON 字符串。提交或中止会消费事务句柄；关闭数据库会丢弃其尚未结束的事务句柄。

## 发布

手动 workflow 的每一个平台 job 在其原生宿主上构建 `cozo-node` 与 `cozo_c`，上传归档制品。汇总 job 将制品放入两个 package 的约定目录，运行 `npm pack` 与 `dotnet pack`，再仅在显式 `publish=true` 时使用 CI secrets 发布。

本地和 CI 都执行发布物消费者验收：npm 将 `.tgz` 安装到临时项目后分别以 Node.js 和 Bun 运行；NuGet 从本地 `.nupkg` source 经 `PackageReference` restore 后直接运行，不通过 `DYLD_LIBRARY_PATH` 或 `LD_LIBRARY_PATH` 注入库路径。这样实际验证 NuGet 的 RID native asset 选择。

开发期的上层仓库使用 `ProjectReference` 时也必须获得同一 `runtimes/<RID>/native/` 内容；因此 native assets 标记为可复制内容，而不是仅在 pack 时收集的文件。这样 C# 拆包测试能够复用生产加载器，而不需要测试专用的环境变量或手工复制步骤。
