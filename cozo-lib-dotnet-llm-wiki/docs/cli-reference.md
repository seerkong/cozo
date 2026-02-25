# CLI 参考

本页与 `depa-wiki --help` 实际输出逐项对齐（2026-07 实测）。

## 子命令总览

```text
depa-wiki --serve [options]
depa-wiki serve [options]
depa-wiki mcp --stdio [options]
depa-wiki index --repo <path> [options]
depa-wiki wiki --repo <path> --out <dir> [--pipeline legacy|codument-fractal] [options]
depa-wiki tools
depa-wiki call <tool-name> [--arguments-json json] [--tool-arg value] [options]
depa-wiki hook augment [--budget <chars>] [options]
depa-wiki hook staleness [options]
depa-wiki hooks install|uninstall|status [--work-dir <path>]
depa-wiki skills generate|clean|status [--work-dir <path>] [--target-dir <path>] [--max-skills <n>] [--budget <chars>]
```

## 各子命令

### `index --repo <path>`

扫描仓库写入 CodeKnowledge 图谱。索引选项：

| 选项 | 含义 | 默认 |
|---|---|---|
| `--doc-symbol-link-mode off\|local\|global` | 文档→符号链接推断模式 | `local` |
| `--max-doc-links-per-doc <n>` | 每个 doc block 最多推断链接数 | `20` |
| `--max-inferred-relations <n>` | 每次索引最多推断关系数 | `200000` |

`local`（默认）只链接同目录/README 覆盖目录/docs 路径 token 相关的文档与符号；`off` 不生成推断 `documents` 关系；`global` 启用全局文本匹配但仍受上限约束。全局弱文本召回请改用 `semantic_search`。

### `wiki --repo <path> --out <dir> [--pipeline legacy|codument-fractal]`

从已索引图谱生成 Markdown 文档树到 `--out`。Wiki 选项：

| 选项 | 含义 | 默认 |
|---|---|---|
| `--pipeline legacy\|codument-fractal` | `legacy` 模板编译器；`codument-fractal` 四阶段社区主导双分形管线 | `legacy` |
| `--use-llm true\|false` | 仅 codument-fractal：使用已配置的 LLM backend，不可用时降级纯结构层 | `true` |
| `--force` | 仅 codument-fractal：忽略页级增量缓存全量重建 | 关 |

未知 `--pipeline` 取值直接报错并列出合法取值，不静默降级。分形管线细节见 [Skills 与 Wiki 生成](skills-and-wiki.md)。

### `mcp --stdio`

以 MCP stdio server 运行，给 coding agent 用。stdout 只输出 JSON-RPC 响应，诊断写 stderr。接入配置见 [MCP 工具参考](mcp-tools.md#mcp-接入配置)。

```bash
depa-wiki mcp --stdio --work-dir /path/to/my/project
```

### `tools` / `call <tool-name>`

`tools` 列出全部 MCP 工具及 JSON schema；`call` 在命令行直接调用同一套工具实现。参数两种传法：

```bash
# kebab-case 选项自动转 camelCase（--repo-path → repoPath）
depa-wiki call semantic_search --work-dir "$REPO" --query "SampleService" --limit 5

# 或直接传 JSON
depa-wiki call semantic_search --work-dir "$REPO" --arguments-json '{"query":"SampleService","limit":5}'
```

全部工具见 [MCP 工具参考](mcp-tools.md)。

### `hook augment` / `hook staleness`

Claude Code hook 的进程入口（读 stdin JSON、写 stdout）。通常不手工调用，由 `hooks install` 写入 `.claude/settings.json` 后被 Claude Code 触发。`augment` 支持 `--budget <chars>`（注入上下文字符预算，默认 2000）。详见 [Hooks 接入](hooks.md)。

### `hooks install|uninstall|status [--work-dir <path>]`

管理 `<work-dir>/.claude/settings.json` 中的 hook 条目，merge-safe（只增删自己的条目，不动其他配置）。详见 [Hooks 接入](hooks.md)。

### `skills generate|clean|status`

从代码图谱生成 Claude Code skills 到 `<work-dir>/.claude/skills`，只写/删 `depa-wiki-*` 前缀目录。选项：`--target-dir`、`--max-skills <n>`、`--budget <chars>`。详见 [Skills 与 Wiki 生成](skills-and-wiki.md)。

### `--serve` / `serve`

本地 HTTP server 模式，服务前端可视化工作台与调试 API：

| 选项 | 含义 | 默认 |
|---|---|---|
| `--host <host>` | 监听地址 | `127.0.0.1` |
| `--port <port>` | 监听端口 | `4176` |
| `--static-dir <path>` | 服务 cozo-lib-dotnet-llm-wiki-viz 构建产物 | — |
| `--api-only true` | 即使有静态资源也只开 API | — |

主要 API：`GET /health`、`GET /api/status`、`GET /api/tools`、`POST /api/tools/call`、`POST /api/index`、`POST /api/embeddings/index`、`POST /api/search/semantic`、`POST /api/graph/overview`、`POST /api/symbol/context`、`POST /api/symbol/impact`、`POST /api/wiki/build`、`POST /api/query/named`。不暴露 raw CozoScript endpoint，高阶查询用 `query_named`。

## 全局存储选项

所有子命令通用：

| 选项 | 含义 | 默认 |
|---|---|---|
| `--engine sqlite\|mem` | CozoDB engine | `sqlite` |
| `--global-dir <path>` | 全局数据/配置基目录 | `~/` |
| `--work-dir <path>` | 项目/工作区目录 | `index`/`wiki` 取 `--repo`；`mcp` 取当前目录 |
| `--data-folder-name <name>` | 数据目录名 | `.depa-wiki` |
| `--db <path>` | 显式 sqlite DB 路径（覆盖默认） | `<work-dir>/.depa-wiki/depa-wiki.db` |

在真实仓库上做 CLI 调用时，建议始终显式传 `--work-dir`，保证所有调用复用同一个 repo DB。
