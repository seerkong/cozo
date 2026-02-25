# 方案设计：Claude Code agent hooks

## 上下文

Claude Code hooks 协议：settings.json 的 hooks.{PostToolUse,SessionStart,...}[].hooks[] = {type:"command", command}；hook 进程 stdin 收 JSON（PostToolUse 含 tool_name/tool_input/tool_response），stdout 可输出 JSON（hookSpecificOutput.additionalContext 注入上下文）；非零 exit 或 stderr 会打扰 agent——故一律 exit 0。GitNexus 参照：PostToolUse 富化 grep 结果（native_augment）。

## 方案概览

1. **hook 子命令**（McpServer CLI 新分支 `hook <augment|staleness>`，存储选项沿用全局 CLI 选项）
   - 共同纪律：顶层 try/catch → stderr 诊断 + 空 stdout + exit 0；无 .cozo-wiki 库直接空退；执行超时自我约束（augment 内部预算控制，不做进程级超时——由 settings timeout 兜底）。
   - `staleness`：读 ck_meta.indexed_commit + git rev-parse HEAD（复用 detect_changes 的读法）；落后 → stdout JSON `{hookSpecificOutput:{hookEventName, additionalContext:"cozo-wiki index is N commits behind HEAD; run cozo-wiki index ..."}}`；一致/无 git → 空。
   - `augment`：解析 stdin JSON → tool_name ∈ {Grep,Glob}（其余空退）→ 从 tool_response 文本提取 `path[:line]` 命中（有界前 10 文件）→ ck_file/ck_symbol 定位（line 落区间的符号，或文件 top 符号）→ 每符号：incoming/outgoing CALLS top3（conf≥0.7、带 name）+ 参与执行流 top2 → 组装紧凑文本（"Graph context (cozo-wiki):" 前缀），字符预算默认 2000（`--budget` 可配）超出截断标注 "…(truncated)"。
   - 查询直连 om（Tools 包既有查询 API：FindSymbolContextAsync/FindProcessesForSymbolAsync），不新增图逻辑。
2. **installer**（`hooks install/uninstall/status`）
   - 目标 `<work-dir>/.claude/settings.json`；JSON merge：hooks.PostToolUse 数组追加自有条目（matcher "Grep|Glob" → command `cozo-wiki hook augment --work-dir <dir>`；staleness 挂 SessionStart matcher startup）；条目辨识：command 含 "cozo-wiki hook" 即自有；uninstall 只删自有；已存在则幂等；文件不存在则创建；解析失败 → 报错不动文件。
3. **E2E**：tests 内以子进程跑 dotnet CLI 或直调 handler（取可测且真实者，倾向直调 handler 函数 + 一个子进程冒烟）；fixture 库复用增量测试的临时 git 仓模式。

## 影响范围与修改点（Impact）

- McpServer/Program.cs（CLI 分支）+ 新 HookCommands.cs（internal）；Tools 无改动（只消费）；tests。

## 决策摘要

- 富化范围 Grep|Glob（Read 噪声大不做）；staleness 挂 SessionStart（每会话一次，避免每工具调用开销）。
- 不默认开启：install 显式（mission eval 先行纪律）。

## 风险 / 权衡

- hook 延迟感 → augment 全链路目标 <500ms（mem 查询 + 有界输出）；预算硬截断。
- settings.json 用户内容破坏 → merge-safe + 解析失败即中止 + uninstall 只删自有。

## 最小公开面

零新增 public 类型（HookCommands internal；CLI 面是命令不是 API）。
