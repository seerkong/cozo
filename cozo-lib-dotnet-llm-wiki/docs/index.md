# depa-wiki 用户手册

`depa-wiki` 把本地仓库索引成代码知识图谱（Cozo OM CodeKnowledge），并以三种方式暴露给使用者：MCP stdio 工具（给 coding agent）、CLI 直接调用（给人工调试）、本地 HTTP server（给可视化工作台）。在此之上还提供 Claude Code hooks 被动注入、skills 生成与 DEPA 架构符合度报告。

## 读者定位

本手册面向要**接入并使用** depa-wiki 的人和 agent：

- 想让 Claude Code / codex 等 coding agent 通过 MCP 用上代码图谱的工程师；
- 直接在命令行调用工具做调试、排障、影响分析的使用者；
- 要解读 DEPA 符合度报告、处置误报的架构评审者。

不面向 depa-wiki 的内部实现开发（那部分见仓库分形文档树与各包源码）。

## 章节导航

| 章节 | 内容 |
|---|---|
| [快速开始](getting-started.md) | 构建、符号链接安装、首次索引、验证 |
| [CLI 参考](cli-reference.md) | 全部子命令与关键选项（与 `--help` 一致） |
| [MCP 工具参考](mcp-tools.md) | 全部 18 个 MCP 工具：用途/参数/真实示例；Claude Code 与 codex 接入配置 |
| [Hooks 接入](hooks.md) | `hooks install/uninstall/status`、augment/staleness、merge-safe 语义、为什么值得装 |
| [Skills 与 Wiki 生成](skills-and-wiki.md) | `skills generate/clean/status` 与 `wiki` 双分形文档生成 |
| [DEPA 报告解读](depa-report.md) | 怎么跑 `depa_conformance`；GAP/PASS/BLOCKED、32 规则分母、误报处置 |
| [故障排查](troubleshooting.md) | 实测已知问题与处置方法 |

## 最短路径

```bash
depa-wiki index --repo /path/to/repo        # 首次索引
depa-wiki tools                             # 看有哪些工具
depa-wiki call semantic_search --work-dir /path/to/repo --query "..." --limit 5
```

接入 agent 见 [MCP 工具参考 · 接入配置](mcp-tools.md#mcp-接入配置)。
