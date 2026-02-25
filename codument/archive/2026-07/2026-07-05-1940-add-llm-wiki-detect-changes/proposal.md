# 变更：detect_changes 工具与 staleness 提示

## 背景和动机 (Context And Why)

mission `expand-llm-wiki-agent-tools`（P1）G4。GitNexus 的 `detect_changes`（git diff → 受影响符号 → 受影响执行流 → 风险评级）与 staleness 检查是把知识图嵌进编码工作流的关键一环——agent 改完代码即可问"我动了什么、波及哪里、索引还新鲜吗"。cozo-wiki 目前无任何 git 集成。P0/G3 已备好映射地基：ck_symbol 行区间、DeepImpactAsync、FindProcessesForSymbolAsync。

## "要做"和"不做" (Goals / Non-Goals)

**目标:**
- git Effect capsule（契约 + 实现分离，DEPA）：IGitDiffProvider——unstaged/staged/compare(base_ref) 三档 diff（unified hunks 解析为 file→行区间集），HEAD commit 读取；进程调用 git CLI 实现；非 git 目录/git 缺失 → 明确诊断不抛。
- staleness：索引时 ck_meta 记 indexed_commit（work dir 是 git 仓时）；api/status 与 detect_changes 响应带 stale 字段（HEAD≠indexed_commit → 提示 reindex）。
- detect_changes 工具（单实现三入口）：scope=unstaged|staged|compare（+baseRef）→ 改动行映射 ck_symbol（file+行区间重叠）→ 每符号 DeepImpact（up，有界）→ 汇总：changedFiles/changedSymbols/impactedSymbols（去重有界）/affectedProcesses/风险（取各符号最高）/stale。
- smoke：临时 git 仓 fixture（init+commit+改动）三 scope 断言。

**非目标:**
- 不做 worktree 参数（P3）。
- 不做 rename/move 检测（新增/删除文件按整文件符号处理）。
- 不自动 reindex（只提示）。

## 变更内容（What Changes）

- 新 Git capsule（Tools 包内 internal 或独立小包——design 定）：契约 + CLI 实现。
- Indexing：索引时写 indexed_commit。
- Tools：detect_changes 注册（矩阵 14→15）；status 响应 stale 字段。

## 影响范围（Impact）

- 受影响能力：llm-wiki-tools（新增 detect-changes 需求）。
- 受影响代码：llm-wiki packages/{Tools,Indexing,Core}、tests。
