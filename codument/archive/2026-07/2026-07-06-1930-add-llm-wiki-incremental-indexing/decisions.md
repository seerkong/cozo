# Decisions

## Usage
- mission frontier（粒度）已按 light 自决文件级；执行期决策追加至此

## T1.1（2026-07-05）
1. **差异来源 = ck_file hash 基线（非 git name-status）**：全量解析免费产出当前 hash，与基线对比对 git/非 git 仓一致且精确；git 区间 diff 在"脏工作区索引后 commit"场景与基线分歧，为一致性弃用。git capsule 仅保留 indexed_commit 记录。
2. **解析全量、facts 选择性删写**：按 design §2 的一致性最简路线；写集 = changed∪added ∪ 双侧边端点扩展文件；removed 另删 ck_file 行。单轮扩展收敛（扩展文件内容未变、重写幂等）。
3. **Summary 计数取 batch 全量口径**（全量路径逐位兼容，增量路径语义为全仓总量）；增量计数 add-only：IncrementalUsed/ChangedFiles(=added+changed)/RemovedFiles/ReusedFiles。
4. **RemoveFileFactsAsync 额外清理 ck_diagnostic**（design 清单外）：诊断 target 为 file/symbol，不清将失败于 T2.1 一致性门禁。

## T2.1（2026-07-06）
1. **一致性门禁通过、提速目标边界未稳达（~2.9x，目标 3x）**：一致性优先原则下不在本 track 深化；深化方向（ParsedFileResult hash 缓存 → Roslyn 增量 → 派生层选择性重算）记 findings，留后续 track。
2. **freshness 断言限定 source_kind="code"**：doc 块（README 等）对已删符号名的提及是合法残留，不属于派生层 stale。
