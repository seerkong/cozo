---
knowledge_plane: domain
doc_role: canonical
status: active
context: llm-wiki
last_verified: 2026-07-06
---

# hooks 注入与 skills 生成流程

## hook augment（PostToolUse: Grep|Glob）

1. 从 stdin 的 hook JSON 中提取 Grep/Glob 工具结果里的文件/行命中。
2. 映射到 `ck_symbol`（按文件+行范围），未命中即静默退出。
3. 查该符号 confidence ≥ 0.7 的 top callers/callees 与参与的执行流（`ck_process`）。
4. 组装 additionalContext 输出，按字符预算截断（默认 2000，`--budget` 可调）。

前置：仓库已索引且 `depa-wiki` 对 Claude Code 进程可解析（PATH）。全程遵守静默降级纪律（[policies/degradation-and-budget.md](../policies/degradation-and-budget.md)）。

## hook staleness（SessionStart: startup）

比对 `ck_meta` 的 indexed commit 与当前 HEAD：落后则输出一条重跑索引的提示，否则空输出。它把"图查询基于旧快照"的风险显式化。

## hooks install / uninstall / status

编辑 `<work-dir>/.claude/settings.json`，注册上面两个 hook 的**裸命令**条目（依赖 PATH 解析）。merge-safe 与 own-entry 语义见 [policies/ownership-safety.md](../policies/ownership-safety.md)；排障见 [hooks-integration.md](../../../../../impl/global/troubleshooting/hooks-integration.md)。

## skills generate / clean / status

1. `generate`：`SkillsGenerator` 从图谱渲染 skill 文件——per-context 导航 skill（社群边界+入口）+ exploring/impact/depa 工作流 skill（附 18 工具矩阵表），确定性有界。
2. `clean`：按自有前缀清理自己生成的 skill，用户手写 skill 零触碰。
3. `status`：列出当前生成物状态。

操作入口与真实输出见用户手册 [skills-and-wiki.md](../../../../../../cozo-lib-dotnet-llm-wiki/docs/skills-and-wiki.md) 与 [hooks.md](../../../../../../cozo-lib-dotnet-llm-wiki/docs/hooks.md)。
