---
knowledge_plane: global
doc_role: troubleshooting
status: active
last_verified: 2026-07-06
---

# hooks 接入排障

面向本仓维护者的排障速查；面向使用者的完整版（含真实症状输出）是用户手册 [troubleshooting.md](../../../../cozo-lib-dotnet-llm-wiki/docs/troubleshooting.md)，本文引用其结论并补维护视角。

## Hook 装了没反应（最常见）

**根因是设计属性**：hook 静默降级——任何失败（无索引库、无符号命中、内部错误）stdout 空、exit 0，诊断只写 stderr（语义真源：[degradation-and-budget policy](../../../modeling/domain/contexts/llm-wiki/policies/degradation-and-budget.md)）。排查顺序：

1. `depa-wiki hooks status --work-dir <repo>` 确认 `.claude/settings.json` 条目在位（PostToolUse [Grep|Glob] + SessionStart [startup]）。
2. 确认已索引：存在 `<repo>/.depa-wiki/depa-wiki.db`。
3. 手工复现看 stderr：`echo '<hook JSON>' | depa-wiki hook augment --work-dir <repo>`。
4. 确认 Claude Code 进程能解析 `depa-wiki`（下一节）。

## PATH / 裸命令问题

installer 写入 settings.json 的是**裸命令** `depa-wiki hook …`，依赖 PATH：

- 安装用**符号链接**指向构建输出目录（二进制依赖同目录 dll，拷贝单文件会启动失败）：`ln -sf <构建输出>/depa-wiki ~/.local/bin/depa-wiki`；
- GUI 启动的 Claude Code 其 PATH 可能与终端不同，需确认 `~/.local/bin` 对其可见；
- 重新 `dotnet build` 后符号链接自动生效。

## 索引落后 HEAD

staleness hook 或 `detect_changes` 报 `stale:true`：重跑 `depa-wiki index --repo <repo>`（增量，通常很快，见 [索引运维](../howto/index-and-incremental-ops.md)）。落后期间图查询基于 `indexedCommit` 快照，行号/新符号可能对不上工作区——augment 注入的上下文同样受此影响。

## TSCLI001（tree-sitter CLI 缺失）

`parser_status`/`parse_file` 报 TSCLI001：本机需自行安装 `tree-sitter` CLI 与对应 grammar（`tree-sitter --version` 验证）。缺失**不回退 Roslyn**、只影响 parse 类工具；基础索引与检索可用，但非 .NET 仓库（如 TS）符号/调用边召回会明显偏低。

## 卸载/重装安全性

`hooks install/uninstall` 是 merge-safe + own-entry 的（只动 `depa-wiki hook` 前缀条目），可放心在已有 `.claude/settings.json` 的项目里反复装卸——若发现动了别人的条目属产品缺陷（语义真源：[ownership-safety policy](../../../modeling/domain/contexts/llm-wiki/policies/ownership-safety.md)）。

## Prevention

新机器接入按用户手册[快速开始](../../../../cozo-lib-dotnet-llm-wiki/docs/getting-started.md)走符号链接安装；CI/会话开始处保留 staleness 提示不要静音。
