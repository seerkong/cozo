# 方案设计：文件级增量索引

## 上下文

现状：RepositoryIndexer.IndexAsync 全量（BuildBatchAsync 解析全仓 → InitCodeKnowledge → 全量 :put → 派生层重算）。已有：ck_file.hash、ck_meta.indexed_commit + staleness（P1）、per-file 事实的 file 归属（ck_symbol.file_id、ck_edge.file_id、ck_doc_block.file_id、ck_external_call 经 caller→symbol→file、ck_entry_point 经 symbol→file）。

## 方案概览

1. **差异计算**（internal IncrementalDiff）
   - 优先 git：indexed_commit..HEAD 的 name-status（A/M/D/R）∩ 索引范围（gitignore/扩展过滤沿用 BuildBatch 的文件枚举）；git 不可用或 indexed_commit 缺 → hash 兜底：枚举当前文件算 hash 对比 ck_file 行（新增/变更/删除）。
   - 未提交改动：git 路径同时并入 working tree 的 diff（unstaged/staged）——与 detect_changes 的 diff capsule 复用（GitChangeReader 若已存在则复用，记 findings）。
2. **文件级重建**
   - Om.CodeKnowledge 增 internal `RemoveFileFactsAsync(fileIds)`：按 file_id 删 ck_symbol/ck_edge（from 或 file_id 归属）/ck_doc_block/ck_entry_point（symbol∈file）/ck_external_call（caller∈file）；ck_file 行删除（removed）或待重写（changed）。
   - changed/added 文件重新走解析（tree-sitter/regex per-file，天然文件粒度）。
   - **跨文件正确性**（关键）：CallResolver 与 doc-link 都依赖全仓上下文。MVP 策略：解析只做增量（复用未变文件的 ParsedFileResult 不可得——解析快不是瓶颈？本仓 build 3.5s 主要在 Roslyn；tree-sitter 解析全仓 <1s）。**实测决定**：若 tree-sitter 全仓解析本身够快，增量只省"未变文件的事实删写与 Roslyn"，解析仍全量、消解仍全量、但 facts 写入只针对受影响文件集（changed∪removed∪其边端点文件）——保证一致性最简。以 dogfood 数字验证提速是否达标（目标：单文件改动端到端 ≥3x 提速），不达标再深化（缓存 ParsedFileResult 按 hash）。此裁定记 findings/decisions。
   - Roslyn：MVP 全量重跑（编译需全仓语法树），归并沿用（其边按调用点 key 覆盖，天然幂等）。
3. **一致性门禁**：测试辅助 DumpFacts（全 ck_* 表规范化序列化，排除 indexed_at/detected_at 类时间戳）——增量后 vs 全新全量逐表 diff。
4. **接线**：RepositoryIndexRequest.IncrementalMode（"auto"|"full"，默认 auto）；IndexAsync 开头判定路径；Summary add-only：ChangedFiles/RemovedFiles/ReusedFiles/IncrementalUsed。
5. **派生层**：沿用索引尾部全量重算（社群 48ms/process 20ms/search text 快）。

## 影响范围与修改点（Impact）

- Indexing（IncrementalDiff + IndexAsync 分支）、Om.CodeKnowledge（RemoveFileFactsAsync internal + IVT）、Core（add-only）、tests。

## 决策摘要

- 文件级粒度（mission frontier light 自决）；一致性优先于提速；提速目标 ≥3x（单文件改动，本仓），不达标深化缓存。

## 风险 / 权衡

- 跨文件边残留/丢失 → 一致性门禁 + 边端点文件集扩大重写。
- git 与 hash 两路径分歧 → 两路径各有一致性测试。

## 最小公开面

public 仅 RepositoryIndexRequest.IncrementalMode 与 Summary add-only 字段；IncrementalDiff/RemoveFileFactsAsync internal。
