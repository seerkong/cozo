# 变更：文件级增量索引

## 背景和动机 (Context And Why)

mission `harden-llm-wiki-engineering`（P3）G1。cozo-wiki 目前每次全量重索引（本仓 ~4s，千文件仓外推 ~分钟级）；GitNexus 有 chunk 缓存 + staleness，规划 symbol 级。已有地基：ck_meta.indexed_commit + staleness（P1 detect_changes 产物）、ck_file.hash 列、分形 wiki 页级增量（P2）。本 track 做**文件级**增量（mission decision frontier 按 light 自决：文件级 MVP，GitNexus 同粒度；符号级留后续）——增量正确性优先于性能（mission design 约束 2：增量结果必须与全量一致，抽样 diff 校验为硬门禁）。

## "要做"和"不做" (Goals / Non-Goals)

**目标:**
- 差异计算：ck_file.hash 对比（git 不可用时兜底）+ git diff indexed_commit..HEAD（可用时优先）→ changed/added/removed 文件集。
- 文件级重索引：removed/changed 文件的 ck_symbol/ck_edge/ck_doc_block/ck_external_call/ck_entry_point 按 file 归属删除 → changed/added 重新解析入库；跨文件影响处理：CallResolver 依赖全仓符号注册表——增量模式下重跑消解但只重写"受影响文件集 ∪ 其边涉及文件"的边（以正确性为先，实现取简单正确者记 findings）。
- Roslyn 增强：MVP 全量重跑语义遍历（编译本身需全仓），仅归并受影响文件的边（记录耗时占比，符号级优化留后续）。
- 派生层重算：社群/执行流/search text 沿用既有全量重算（本仓实测均 <100ms，不是瓶颈）。
- 一致性硬门禁：IncrementalConsistencyCheck 模式（测试/dogfood 用）——增量后全库 facts 与全量重索引逐表 diff 一致。
- RepositoryIndexRequest.IncrementalMode（auto|full，默认 auto：有 indexed_commit 且 git 可用走增量，否则全量）；Summary add-only 计数（ChangedFiles/RemovedFiles/ReusedFiles、增量耗时）。
- dogfood：本仓单文件改动增量 vs 全量耗时对比 + 一致性校验。

**非目标:**
- 符号级增量（后续）。
- embedding 增量（index_embeddings 已是显式独立步骤，本 track 不动；staleness 提示已有）。
- 自动 re-index 守护（mission 非目标）。

## 变更内容（What Changes）

- Indexing：差异计算 + 文件级删除/重建 + 增量模式接线；Core add-only。
- Om.CodeKnowledge：按 file 删除 facts 的 API（internal 或最小公开，design 定）。
- tests 两侧（一致性门禁测试）。

## 影响范围（Impact）

- 受影响能力：dotnet-llm-wiki（新增 incremental-indexing 需求）。
- 受影响代码：packages/{Indexing,Core}、cozo-lib-dotnet/src/Om.CodeKnowledge/、tests。
