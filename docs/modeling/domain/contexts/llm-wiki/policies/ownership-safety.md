---
knowledge_plane: domain
doc_role: canonical
status: active
context: llm-wiki
last_verified: 2026-07-06
---

# 生成物写盘的所有权与合并安全

## Rule

任何往用户目录/配置写入生成物的路径，只能**创建、更新、删除属于自己的条目**，绝不触碰用户或其他工具的内容。

## 具体化

- **hooks install/uninstall 是 merge-safe 的**：只编辑 `<work-dir>/.claude/settings.json` 中命令以 `depa-wiki hook` 开头的条目；已有的其他 hooks、permissions 等原样保留。uninstall 的 own-entry 判据同样是命令前缀——绝不误删他人 hook。
- **skills clean 是 own-prefix-only 的**：只清理自己生成的 skill（按自有前缀识别），用户手写的 skill 文件零触碰（用户目录零触碰有专门测试）。
- **wiki 自举骨架不覆盖人工树**：生成到独立输出目录，与人工维护的 docs/ 真源树的合并由人工完成（本仓 P2 即按此执行）。

## Rationale

这些写入点都在用户的共享配置里（`.claude/settings.json`、skills 目录、docs 树）。生成器对"什么是自己的"必须有可机判的判据（命令前缀/文件前缀/独立目录），否则一次重装或清理就可能吞掉用户内容——安装可重复、卸载可放心是被采用的前提。

## Exceptions

无。需要接管非自有条目的场景应显式让用户操作，工具不代劳。

## Enforcement Points

- `HookCommands`（install-merge-safe 测试 + 子进程冒烟）。
- `SkillsCommands` / `SkillsGenerator`（own-prefix-only 与用户目录零触碰测试）。
- 操作入口见用户手册 [hooks.md](../../../../../../cozo-lib-dotnet-llm-wiki/docs/hooks.md) 与 [skills-and-wiki.md](../../../../../../cozo-lib-dotnet-llm-wiki/docs/skills-and-wiki.md)。
