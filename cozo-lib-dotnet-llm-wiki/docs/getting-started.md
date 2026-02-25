# 快速开始

## 1. 构建

需要 .NET SDK（当前目标框架 net10.0）。在仓库根目录：

```bash
dotnet build cozo-lib-dotnet-llm-wiki/Cozo.DotNet.LlmWiki.slnx -c Release
```

跑测试（可选，验证构建健康）：

```bash
dotnet run --project cozo-lib-dotnet-llm-wiki/tests/Cozo.DotNet.LlmWiki.Tests/Cozo.DotNet.LlmWiki.Tests.csproj
```

## 2. 安装为 `depa-wiki` 命令（符号链接）

构建产物在 `packages/Cozo.DotNet.LlmWiki.McpServer/bin/Release/net10.0/depa-wiki`。推荐用符号链接放进 PATH，而不是复制二进制——链接指向构建输出目录，重新 `dotnet build` 后无需重装：

```bash
ln -sf "$(pwd)/cozo-lib-dotnet-llm-wiki/packages/Cozo.DotNet.LlmWiki.McpServer/bin/Release/net10.0/depa-wiki" \
  ~/.local/bin/depa-wiki
depa-wiki --help   # 验证
```

确保 `~/.local/bin` 在 PATH 中。注意：可执行文件依赖同目录的一组 dll，所以必须用符号链接（或写全路径），**不能**把单个二进制拷走——这是实测踩过的坑，详见[故障排查](troubleshooting.md#depa-wiki-不在-path-或拷贝后启动失败)。

## 3. 首次索引

```bash
REPO=/path/to/my/project
depa-wiki index --repo "$REPO"
```

索引写入 `<repo>/.depa-wiki/depa-wiki.db`（默认 sqlite engine）。建议把 `.depa-wiki/` 加入该仓库的 `.gitignore`。

索引会读取仓库根的 `.gitignore` 跳过匹配文件，并内置排除 `.git`、`bin`、`obj`、`node_modules`、`dist`、`build`、`.next` 等目录。

如需语义检索，再建一次 embedding 索引：

```bash
depa-wiki call index_embeddings --work-dir "$REPO" --include-symbols true --include-docs true
```

## 4. 验证

```bash
# 工具清单能列出即安装成功
depa-wiki tools

# 对已索引仓库做一次真实查询
depa-wiki call semantic_search --work-dir "$REPO" --query "main entry" --limit 3
```

## 5. 下一步

- 给 agent 接 MCP：[MCP 工具参考 · 接入配置](mcp-tools.md#mcp-接入配置)
- 给 Claude Code 装被动注入 hooks：[Hooks 接入](hooks.md)
- 生成项目 skills / wiki 文档：[Skills 与 Wiki 生成](skills-and-wiki.md)

## 存储目录说明

`depa-wiki` 默认 `sqlite` engine，两个数据目录：

- 全局目录：`<global-dir>/<data-folder-name>`，默认 `~/.depa-wiki`
- 工作目录：`<work-dir>/<data-folder-name>`，默认 `<work-dir>/.depa-wiki`，DB 文件为其中的 `depa-wiki.db`

`--work-dir` 对 `index`/`wiki` 默认取 `--repo`，对 `mcp` 默认取当前目录。临时内存库用 `--engine mem`；显式 DB 路径用 `--db <path>`。
