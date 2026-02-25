# Mission：harden-llm-wiki-engineering（P3 · 工程化外围）

## 背景和动机

2026-07-04 GitNexus gap 分析中识别的工程化外围能力，均"有价值但不是当前瓶颈"，收拢为 P3 mission，在 P0–P2 主线之后按需启动：

- **增量索引**：GitNexus 有 chunk 级 parse 缓存 + `--repair-fts` + staleness 检查，规划 symbol 级增量（改动文件及上游调用者重算）；cozo-wiki 目前全量重索引。
- **agent hooks**：GitNexus 的 Claude Code PreToolUse hook 把 grep/search 结果自动富化为图上下文（callers + callees + processes）——这是其 SWE-bench eval 中效果最好的 `native_augment` 模式；PostToolUse 做 staleness 提示。cozo-wiki 无 hook 集成。
- **skills 自动生成**：GitNexus 按社群生成 `.claude/skills/generated/<community>/SKILL.md` + 4 个通用工作流 skill（exploring/impact-analysis/debugging/refactoring）。
- **eval 框架**：GitNexus 用 SWE-bench（baseline / native / native_augment 三模式对比）量化工具对 agent 解题率的贡献（eval/ 目录，Python + Docker）。
- **多仓库 group + Contract Bridge**：跨仓库统一图、HTTP 契约提取与跨仓库链接（bridge-db）。
- **配置文件**：`.gitnexusrc`（workers/maxFileSize/embeddings 默认等 per-repo 配置）。

## 目标

1. 增量索引：git diff → 受影响文件/符号重算（含 embedding 增量），staleness 与 P1 的 detect_changes 打通。
2. Claude Code hooks：PostToolUse staleness 提示 + PreToolUse 搜索结果图富化（cozo-wiki 版 native_augment）。
3. skills 生成：按社群/context 生成项目 skill + 通用工作流 skill。
4. 轻量 eval：至少建立"有/无 cozo-wiki 工具"的 agent 任务对比基准（不必照搬 SWE-bench 全家桶）。
5. per-repo 配置文件（.cozo-wikirc 或复用现有 CLI 选项持久化）。
6. 视需求：多仓库 group。

## 非目标

- 不做企业级部署/认证/SaaS。
- 不做自动 re-index 守护进程（先手动 + 提示）。

## 成功判据

- 在 cozo 仓库上：单文件改动后增量索引耗时相对全量有数量级下降。
- hook 装上后，agent 在本仓的实际编码会话中可观察到图上下文注入。
- eval 基准可重复运行并出对比报告。

## 依赖与为什么是 mission

依赖 P0（图）与 P1（detect_changes/staleness）。多条独立工程线（增量/hook/skills/eval/多仓库），按需分批，典型的长周期编排场景。
