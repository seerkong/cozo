# 故障排查

以下均为实测遇到过的问题与处置方法。

## depa-wiki 不在 PATH，或拷贝后启动失败

**症状**：`command not found: depa-wiki`；或把二进制拷到别处后启动报缺 dll。

**原因与处置**：

- hooks installer 往 `.claude/settings.json` 写的是**裸命令** `depa-wiki hook …`，依赖 PATH 可解析。安装时用符号链接指向构建输出目录（而不是复制单个二进制——它依赖同目录一组 dll）：

```bash
ln -sf "<仓根>/cozo-lib-dotnet-llm-wiki/packages/Cozo.DotNet.LlmWiki.McpServer/bin/Release/net10.0/depa-wiki" \
  ~/.local/bin/depa-wiki
```

- 确认 `~/.local/bin` 在 PATH（对 GUI 启动的 Claude Code，注意其 PATH 可能与终端不同）；
- 重新 `dotnet build` 后符号链接自动生效，无需重装。

## 本地测试编译长时间占用 Roslyn 或留下构建进程

**症状**：`Cozo.DotNet.LlmWiki.Tests` 在 `Csc` / Roslyn 阶段持续占用一个 CPU 核，机器出现明显
内存压缩或 swap；此前异常中止后还可能观察到 MSBuild worker。

**原因与处置**：该项目是一个约两万行的单一测试可执行程序集，引用多个项目。完整 analyzer 与 source
generator 在资源紧张的开发机上会显著放大编译时间；高 CPU 本身不代表死锁。

1. 有限的本地 build/run/test 一律使用 `scripts/dotnet-safe.sh`。它禁用 node reuse、禁用 build server、将 `build`/`test` 限制为一个 MSBuild worker，并在退出时执行 `dotnet build-server shutdown`；对该测试项目默认关闭 analyzers 和并行项目引用构建。
2. CI 与直接 `dotnet` 构建仍保留完整 analyzer；本地需要复现该路径时设 `LLM_WIKI_FAST_LOCAL_TESTS=0`。
3. CLI 子进程测试默认有 90 秒预算，超时会 `Kill(entireProcessTree: true)`；可用 `DEPA_WIKI_TEST_CLI_TIMEOUT_SECONDS` 在 1-300 秒间调整，不能设置为无限期。
4. 若命令被外部中断，执行 `dotnet build-server shutdown` 后再确认没有 `dotnet`、`VBCSCompiler` 或 `MSBuild.dll` 进程；不要用测试 apphost 去启动 `depa-wiki.dll`，否则会递归启动整个测试套件。

## 索引落后 HEAD

**症状**：SessionStart 时 staleness hook 提示索引落后；或 `detect_changes` 输出 `"stale":true`：

```json
"indexedCommit":"bba2fc55…","headCommit":"76dd80c0…","stale":true,
"staleHint":"the index was built at a different commit than the current HEAD; re-run index_repo to refresh"
```

**处置**：重跑索引（增量，只重扫变更文件，通常很快）：

```bash
depa-wiki index --repo "$REPO"
# 或 depa-wiki call index_repo --work-dir "$REPO" --repo-path "$REPO"
```

图查询结果基于 `indexedCommit` 的快照；索引落后时行号与新增符号可能对不上工作区。

## 增量与全量索引结果不一致（一致性门禁）

增量索引设计上与全量重建结果一致（有一致性门禁测试保障）。若怀疑增量索引后图数据异常（如符号残留、关系缺失），处置：

1. 删掉 `<work-dir>/.depa-wiki/depa-wiki.db` 后全量重建一次，对比结果；
2. 若全量与增量确实不一致，这是产品缺陷——保留两份 DB 与复现步骤提 issue，不要自行改库。

## Hook 装了没反应

hook 的设计是**静默降级**：任何失败（无索引库、无符号命中、内部错误）都输出空 stdout、exit 0，不打扰 agent。排查顺序：

1. `depa-wiki hooks status --work-dir <repo>` 确认条目已写入 `.claude/settings.json`；
2. 确认该仓库已索引（存在 `<repo>/.depa-wiki/depa-wiki.db`）；
3. 手工喂 stdin 复现并看 stderr 诊断：`echo '<hook JSON>' | depa-wiki hook augment --work-dir <repo>`；
4. 确认 Claude Code 进程能解析 `depa-wiki`（见上文 PATH 一节）。

## codex 非交互下 MCP 工具全部没被调用

**症状**：`codex exec` 挂了 depa_wiki MCP server，但回答里没有任何工具调用痕迹。

**原因**：非交互模式下 MCP 工具审批被自动 Cancel。**必须**加：

```bash
-c 'mcp_servers.depa_wiki.default_tools_approval_mode="approve"'
```

完整配置见 [MCP 工具参考 · codex](mcp-tools.md#codex)。

## parser_status / parse_file 报 TSCLI001

**症状**：

```json
{"available":false,"parser":"tree-sitter",
 "diagnostics":[{"code":"TSCLI001","message":"tree-sitter CLI is unavailable. Install tree-sitter and grammars…"}]}
```

**处置**：语义解析走本机 `tree-sitter` CLI，需要自行安装 CLI 与对应 grammar（`tree-sitter --version` 验证）。缺失时不回退 Roslyn，只影响 `parse_file` 类工具；基础索引与检索不受影响。

## TypeScript 等非 .NET 仓库注意事项

- 索引器是语言轻量抽取 + Tree-sitter 增强：TS 仓建议装好 `tree-sitter` CLI 与 TypeScript grammar，否则符号/调用边召回明显偏低，`trace`/`impact_of_change` 结果稀疏；
- 大前端仓务必确认 `.gitignore` 覆盖 `node_modules/`、`dist/` 等目录（索引器读根 `.gitignore`，并有内置安全排除），否则 DB 膨胀、索引变慢；
- 文档→符号推断链接保持默认 `--doc-symbol-link-mode local`；要全局弱召回用 `semantic_search`，别开 `global`。

## semantic_search 只有 text 通道 / 没有向量结果

**症状**：hits 里 `channels` 只有 `["text"]`。

**处置**：还没建 embedding 索引，跑 `depa-wiki call index_embeddings --work-dir "$REPO" --include-symbols true --include-docs true`。另外若运行目录缺 ONNX 模型资产，会回退 deterministic embedding provider（可用但语义质量下降）。
