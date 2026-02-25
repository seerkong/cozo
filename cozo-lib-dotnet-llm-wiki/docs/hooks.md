# Hooks 接入（Claude Code）

depa-wiki 提供两个 Claude Code hook，把代码图谱知识**被动**注入 agent 会话，agent 无需主动调工具也能受益：

| Hook | 触发点 | 做什么 |
|---|---|---|
| `hook augment` | PostToolUse（Grep\|Glob） | 从工具结果提取文件/行命中 → 映射到 ck_symbol → 查 top callers/callees（confidence≥0.7）与参与执行流 → 输出有界 additionalContext（字符预算默认 2000，`--budget` 可调） |
| `hook staleness` | SessionStart（startup） | 比对索引 commit 与 HEAD，索引落后时输出一条提示（提醒重跑索引），否则空输出 |

## 为什么值得装

真实 agent 双模式 eval（codex，24 题 × 2 模式，2026-07-06）量化了图谱工具的贡献：

| 模式 | avg score | must 要素命中率 |
|---|---|---|
| baseline（无工具） | 0.096 | 7.7% |
| native（18 个 depa-wiki MCP 工具） | 0.950 | 96.2% |
| **提升** | **+0.854** | **+88.5pp** |

（报告见 `eval/results/2026-07-06-codex/report.md`。该数据测的是主动 MCP 工具调用；augment hook 的被动注入是其子集场景，且静默降级已验证、风险低。）

## 安装 / 卸载 / 状态

```bash
depa-wiki hooks install   --work-dir /path/to/repo
depa-wiki hooks status    --work-dir /path/to/repo
depa-wiki hooks uninstall --work-dir /path/to/repo
```

`status` 真实输出示例：

```text
Settings file: /path/to/repo/.claude/settings.json
  PostToolUse [Grep|Glob]: depa-wiki hook augment --work-dir /path/to/repo
  SessionStart [startup]: depa-wiki hook staleness --work-dir /path/to/repo
```

## merge-safe 与 own-entry 语义

`hooks install/uninstall` 只编辑 `<work-dir>/.claude/settings.json` 中**自己的**条目：

- **merge-safe**：settings.json 里已有的其他 hooks、permissions 等配置原样保留，只增/删 depa-wiki 的 hook 条目；
- **own-entry**：uninstall 只移除命令以 `depa-wiki hook` 开头的条目，绝不误删用户或其他工具写入的 hook。

因此可以放心在已有 `.claude/settings.json` 的项目里安装。

## 静默降级（非阻塞纪律）

hook 进程的任何异常——无索引库、符号未命中、内部错误——都静默降级：**stdout 输出空、exit 0**，诊断只写 stderr。agent 会话不会被 hook 失败打断或注入错误信息。代价是"hook 没生效"不易察觉，排查方法见[故障排查](troubleshooting.md)。

## 前置条件

- 目标仓库已跑过 `depa-wiki index --repo <repo>`（augment 需要 ck_symbol 图，staleness 需要 indexed commit）；
- `depa-wiki` 对 Claude Code 进程可见（PATH 中的符号链接，见[快速开始](getting-started.md#2-安装为-depa-wiki-命令符号链接)）。
