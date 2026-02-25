# 方案设计：detect_changes 与 staleness

## 上下文

可复用：ck_symbol(file_id, start_line, end_line)、ck_file(path)、DeepImpactAsync（up/有界/风险）、FindProcessesForSymbolAsync、ck_meta（schema_version/indexed_at 已有）。GitNexus 参照：detect_changes 的 diff→符号映射与 staleness hint。

## 方案概览

1. **Git capsule**（Tools 包内，internal——工具面是唯一消费者，不值得独立包）
   - 契约 `IGitDiffProvider`（internal interface）：`TryGetHeadCommit(dir)`、`GetDiff(dir, scope, baseRef)` → `GitDiffResult(Files: [path → 变更行区间集], Diagnostics)`。
   - 实现 `GitCliDiffProvider`：Process 调 `git -C <dir> diff -U0 [--cached | <base>...HEAD]`；`-U0` hunk 头 `@@ -a,b +c,d @@` 解析新侧行区间；新增文件全区间；删除文件标记 deleted（映射其全部符号）。git 不存在/非仓库 → Diagnostics 且结果空（不抛）。
   - 测试 seam：runner 上可注入 provider。
2. **staleness**
   - Indexing：IndexAsync 时 TryGetHeadCommit → ck_meta.indexed_commit（非 git 则不写）。Indexing 需要 provider——把 Git capsule 放 Tools 会形成 Tools→Indexing 反向？否：Indexing 不引用 Tools。改放 **Core 包**（internal + InternalsVisibleTo(Tools, Indexing, Tests)）——Core 无依赖、两侧均可用。
   - status/detect_changes：读 ck_meta.indexed_commit vs TryGetHeadCommit → stale 字段 + 提示文本。
3. **detect_changes 工具**（Tools，矩阵 15）
   - 参数：scope=unstaged|staged|compare（默认 unstaged）、baseRef（compare 用，默认 origin/HEAD 解析失败退 main/master 再失败报诊断）、maxImpactDepth（默认 2）、minConfidence（默认 0.0）。
   - 流程：diff → path 归一化映射 ck_file → 行区间与 ck_symbol 区间重叠 → changedSymbols（有界 100+截断计数）；每符号 DeepImpactAsync(up, maxDepth) → impactedSymbols 去重（≤200+截断）、affectedProcesses 去重（≤20）；risk=各符号 risk 最高；stale 字段。
   - 输出含 changedFiles（含未映射文件列表——比如非索引语言）。
4. **smoke**：CliWrap 式临时 git 仓 fixture（git init/config/commit 若环境无 git 则跳过该组并记录——git 在本机可用）；三 scope + stale + 非 git 目录 + impact 聚合断言。

## 影响范围与修改点（Impact）

- Core：Git capsule（internal）+ InternalsVisibleTo。
- Indexing：indexed_commit 写入。
- Tools：detect_changes + status stale。
- tests。

## 决策摘要

- Git capsule 落 Core（internal）：唯一让 Tools 与 Indexing 共享且不引入新包/反向依赖的位置；契约+实现分离保留（DEPA）。
- `-U0` 精确行区间；rename 检测非目标。

## 风险 / 权衡

- diff 解析边界（\r\n、二进制文件）→ 二进制跳过记 diagnostics；测试覆盖。
- baseRef 解析在裸环境不稳 → 显式 baseRef 优先，默认链失败给诊断。

## 最小公开面

工具输出 schema 为公开契约；Git capsule/解析器全部 internal；Core 无新增 public 类型（除非 Summary/Request 需要 add-only 字段——本 track 不需要）。
