# 变更：Claude Code agent hooks（搜索图富化 + staleness）

## 背景和动机 (Context And Why)

mission `harden-llm-wiki-engineering`（P3）G2。GitNexus 的 SWE-bench eval 里效果最好的 `native_augment` 模式 = PostToolUse hook 把 agent 的 grep/搜索结果自动富化为图上下文（callers/callees/processes）；PostToolUse 另做 staleness 提示。cozo-wiki 的图与工具面已就位（P0/P1），本 track 把它们接进 Claude Code hook 协议，让 agent 在普通编码会话中免工具调用即获得图上下文。

## "要做"和"不做" (Goals / Non-Goals)

**目标:**
- hook CLI 子命令（cozo-wiki 的新 CLI 面，走既有 CLI 入口）：
  - `cozo-wiki hook staleness`：读 stdin hook JSON（或直接查库），indexed_commit vs HEAD 落后时输出提示 JSON（additionalContext/systemMessage），否则空输出；<200ms 目标。
  - `cozo-wiki hook augment`：读 stdin 的 PostToolUse JSON（Grep/Glob 结果），提取文件/行命中 → 映射 ck_symbol → 查 top callers/callees（conf≥0.7）与参与执行流 → 输出有界 additionalContext（字符预算默认 2000，可配）；无命中/无库/任何错误 → 空输出 exit 0。
- 非阻塞纪律（mission design 约束 3）：hook 进程任何异常静默降级（stderr 留诊断、stdout 空、exit 0），绝不阻塞 agent。
- installer：`cozo-wiki hooks install [--work-dir]`：merge-safe 写目标仓 .claude/settings.json 的 hooks 配置（PostToolUse matcher Grep|Glob → augment；SessionStart 或 PostToolUse → staleness）；`hooks uninstall` 反向移除自己写入的条目；不覆盖用户已有 hooks。
- E2E：模拟 stdin hook JSON 对真实索引库跑两个子命令断言输出结构与预算；本仓 dogfood 演示记录。

**非目标:**
- 不默认开启（mission design：eval 量化后再默认）——install 是显式动作。
- 不做 Cursor/其他编辑器 hook（Claude Code 先行）。
- 不做 PreToolUse 拦截/改写（只做 PostToolUse 富化与提示）。

## 变更内容（What Changes）

- McpServer（CLI 入口）：hook/hooks 子命令；Tools：富化查询复用既有 runner 能力（零新图逻辑）。
- tests：hook 协议 E2E。

## 影响范围（Impact）

- 受影响能力：llm-wiki-tools（新增 agent-hooks 需求）。
- 受影响代码：packages/{McpServer,Tools}、tests。
