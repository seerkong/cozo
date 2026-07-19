# Cozo .NET LLM Wiki

`cozo-lib-dotnet-llm-wiki` 把本地仓库扫描为 Cozo OM 代码/文档知识图谱（基于 `cozo-lib-dotnet`、`Om.CodeKnowledge`、`Om.Query`），并通过 MCP stdio / CLI / 本地 HTTP server 三种形态暴露给 coding agent、人工调试与可视化工作台使用。

## 特性

| 能力 | 说明 |
|---|---|
| 代码知识图谱 | `index` 增量索引仓库为 ck_* 符号/关系/文档图（sqlite，默认 `<repo>/.depa-wiki/`） |
| 18 个 MCP 工具 | 检索（semantic_search）、图查询（symbol_context/trace/impact_of_change/check/detect_changes 等）、DEPA 符合度（depa_conformance/health_score/fact_grade_map） |
| Claude Code hooks | `hooks install` 一键接入被动注入（augment/staleness），merge-safe、静默降级 |
| Skills / Wiki 生成 | `skills generate` 生成 Claude Code skills；`wiki` 生成双分形 Markdown 文档树 |
| 本地 Server + 前端 | `--serve` 提供 HTTP API，配套 `cozo-lib-dotnet-llm-wiki-viz/` 可视化工作台 |

实测价值：真实 agent 双模式 eval 中，接入 18 个 MCP 工具使 codex 回答仓库结构问题的 avg score 从 0.096 提到 0.950（must 要素命中率 7.7%→96.2%），见 `eval/results/2026-07-06-codex/report.md`。

## 快速开始

```bash
# 构建并用符号链接装进 PATH
dotnet build cozo-lib-dotnet-llm-wiki/Cozo.DotNet.LlmWiki.slnx -c Release
ln -sf "$(pwd)/cozo-lib-dotnet-llm-wiki/packages/Cozo.DotNet.LlmWiki.McpServer/bin/Release/net10.0/depa-wiki" ~/.local/bin/depa-wiki

# 索引并验证
REPO=/path/to/my/project
depa-wiki index --repo "$REPO"
depa-wiki call semantic_search --work-dir "$REPO" --query "main entry" --limit 3

# 给 agent 接 MCP
depa-wiki mcp --stdio --work-dir "$REPO"
```

## 用户手册

完整使用文档在 [docs/](docs/index.md)：

- [快速开始](docs/getting-started.md) — 构建、安装、首次索引、验证
- [CLI 参考](docs/cli-reference.md) — 全部子命令与选项
- [MCP 工具参考](docs/mcp-tools.md) — 18 个工具逐一说明 + Claude Code / codex 接入配置
- [Hooks 接入](docs/hooks.md) — 被动注入的安装与语义
- [Skills 与 Wiki 生成](docs/skills-and-wiki.md)
- [DEPA 报告解读](docs/depa-report.md) — GAP/PASS/BLOCKED、32 规则分母、误报处置
- [故障排查](docs/troubleshooting.md)

## 开发者

包结构（`packages/`）：

- `Core`：纯模型与配置，不依赖 Cozo。
- `Indexing`：仓库扫描、符号/文档抽取、CodeKnowledge 写入。
- `SemanticParsing`：Tree-sitter-only 语义解析（经 `tree-sitter` CLI adapter）。
- `VectorSearch`：embedding provider 抽象、本地 ONNX MiniLM、Cozo 向量 relation 与 semantic search。
- `Wiki`：wiki 编译与 skills 生成。
- `Tools`：MCP、CLI、HTTP 共用的 tool runner、metadata、storage options 与参数绑定。
- `Server`：ASP.NET Core 本地 HTTP API 与静态资源。
- `McpServer`：CLI 与 MCP stdio 主入口。

构建与测试：

```bash
cozo-lib-dotnet-llm-wiki/scripts/dotnet-safe.sh build cozo-lib-dotnet-llm-wiki/Cozo.DotNet.LlmWiki.slnx
cozo-lib-dotnet-llm-wiki/scripts/dotnet-safe.sh run --project cozo-lib-dotnet-llm-wiki/tests/Cozo.DotNet.LlmWiki.Tests/Cozo.DotNet.LlmWiki.Tests.csproj
```

`scripts/dotnet-safe.sh` is required for finite local build/test automation: it disables
MSBuild node reuse and shuts down build servers on exit. It deliberately rejects `dotnet watch`
and long-lived application servers. The test CLI helper also has a 30-second timeout and kills
its entire child process tree; do not replace its explicit `dotnet depa-wiki.dll` host with
`Environment.ProcessPath`, which is the test apphost and would recursively start the suite.

前端工作台开发见 `cozo-lib-dotnet-llm-wiki-viz/`（`VITE_API_BASE=http://127.0.0.1:4176 npm run dev`，或 `npm run build` 后用 `--serve --static-dir` 服务）。
