# Mission Design：deepen-llm-wiki-code-graph

## 控制论模型

- desired state：mission.xml 顶层 TaskGroup DAG（证据盘点/设计收敛 → 解析 ingestion → 调用消解 → 聚类与执行流 → 验证收口），叶子 Task 上的 `cdt:TrackLink`。
- actual state：codument/tracks 与 archive 中相关 track 状态、cozo-lib-dotnet-llm-wiki 代码与测试结果、reports、用户决策。
- actuation：创建/续跑/归档 track，或受控修订 mission.xml。
- feedback / drift：track reports、smoke/E2E 结果、解析质量抽样、用户介入。

## Mission Actors

| Actor | 控制论角色 | DEPA 归属 | 职责 |
|---|---|---|---|
| MissionPlanner | 期望态产出者 | Processor + Actor | 产出/修订 desired mission graph（分批 track 切片） |
| MissionObserver | 传感器 | Data + Actor | 读取 track/archive/代码/测试/报告的 actual state projection |
| MissionReconciler | 控制器 | Processor + Actor | 比较 desired vs actual，判定 drift / ready / blocked / done |
| MissionApplier | 执行器 | Effect + Actor | 执行一个 bounded action（建 track、跑 track、归档、replan） |

## plan vs track 区分

mission 只负责控制面：证据盘点、选型决策、track 切片与编排、阶段验证。所有解析器集成、schema 变更、算法实现、测试由真实 track 承担。无例外的 mission 内直接实现任务。

## 关键设计约束（来自 gap 分析与项目理念）

1. **DEPA/capsule 纪律**（延续 cozo-lib-dotnet 既有架构）：解析器接入走 contract + impl 分离——解析抽象契约独立于具体后端（tree-sitter native / Roslyn / CLI），落图逻辑不感知解析器实现；per-language extractor 是 GitNexus 已验证的接缝模式（`scope-resolution/contract/scope-resolver.ts` 可参考）。
2. **图 schema 演进兼容**：ck_* relations 属 OM CodeKnowledge capsule；新增边类型（CALLS/EXTENDS/HAS_METHOD/ACCESSES/MEMBER_OF/STEP_IN_PROCESS）与 confidence 字段需保持既有 12 个 MCP 工具和 tests 不回归。
3. **confidence 纪律**（借 GitNexus）：调用边必须带置信度（≥0.8 高置信 / 以下 fuzzy），下游工具按 minConfidence 过滤——宁可低置信标注也不静默丢弃。
4. **有界产出**：社群/执行流数量必须有动态上限（GitNexus：maxProcesses = max(20, min(300, symbolCount/10))，最小步数 3），延续 optimize-cozo-wiki-relation-generation track 的防膨胀经验。
5. **CozoDB 优先**：图算法优先评估 CozoDB 内置（Datalog 固定点递归、community detection 等），确无法满足再落 .NET 侧实现——这是相对 GitNexus 的差异化（他们要自己维护 Graphology）。

## 关键决策（2026-07-04 用户确认，详见 decisions.md / analysis/decision-tree.md）

- D1 解析器 = **混合**：tree-sitter 原生 P/Invoke 统一多语言基线 + Roslyn 增强 C# 调用消解置信度；两后端实现同一解析契约，落图逻辑不感知实现；CLI adapter 降级为备选后端。
- D2 首批语言 = **C# + TS/JS**（本仓自身 dogfood）。
- D3 schema = **v2 重设计 + 迁移**：参照 GitNexus 节点/边类型全集裁剪，边带 confidence；迁移以重建索引 + 工具适配为主，12 工具全量回归为硬门禁。
- D4 社群检测 = **CozoDB 内置 Louvain**（cozo-core fixed_rule 已查证），聚类策略可替换，为 Leiden 留接缝。
- D5 执行流入口 = **public API 面 + Main/CLI + ASP.NET 路由/MCP 工具定义**；测试入口后置。

## 落地切片与验收门禁（G1-T4 定稿，2026-07-04）

spike 证据：tree-sitter P/Invoke 可行（3 dylib/平台 ~7.2MB，query API 可用，需 pin grammar commit 并断言 ABI 版本）；Roslyn 轻量编译（ParseText+Compilation，非 MSBuildWorkspace）对本仓 2678 调用点 confidence=1.0 命中 98.8%，符号 canonical ID 用 DocumentationCommentId，双后端归一键 sym_key=(lang, 全限定名, arity)。

v2 schema 要点（全文 analysis/schema-v2-design.md，将随 G2 track proposal 进 git）：单边表 ck_edge{from,to,kind,file,line => confidence,resolver,evidence}；ck_symbol 加 sym_key/doc_id/parent_id/resolver；派生层专表 ck_community/ck_member/ck_process/ck_process_step/ck_entry_point；ck_meta 记 schema_version；重建式迁移（--reindex）。

track 切片与门禁：
1. `redesign-codeknowledge-schema-v2`（G2）：v2 relations + 迁移 + 12 工具适配（先由现有 regex 索引器喂 ck_edge，保持工具链存活）。门禁：dotnet + llm-wiki 全部 tests/smoke 通过。
2. `add-llm-wiki-treesitter-ingestion`（G3）：解析后端契约 + tree-sitter P/Invoke 后端 + C#/TS extractor，符号与结构边入图，替换正则主路径。门禁：对本仓索引符号数 ≥ 正则版且结构边抽样正确。
3. `add-llm-wiki-call-resolution` + `add-llm-wiki-roslyn-csharp-resolution`（G4）：CALLS/ACCESSES/OVERRIDES 与 Roslyn 归并。门禁：confidence≥0.8 的 CALLS 边抽样 30 条正确率 ≥90%。
4. `add-llm-wiki-community-detection` + `add-llm-wiki-process-extraction`（G5）：Cozo Louvain 固定规则 + D5 入口点。门禁：社群/Process 有界（预算模式）且标签可解释。
5. `verify-llm-wiki-code-graph-e2e`（G6）：端到端 + 12 工具回归 + 行为沉淀。

## 受控重规划

active 后可增删改节点，但必须有 evidence 或 human decision，写 reports/replan-XXX.md。预期主要重规划源：解析器选型验证失败（D1 回退路径）、Cozo 内置算法能力不足（D4 回退路径）。

## 人工介入点

- D1–D5 决策确认（severity light：只上 P0/不可逆项）。
- 每批 track 落地后的质量抽样（调用边正确率）。

## 风险

| 风险 | 缓解 |
|---|---|
| tree-sitter .NET 绑定生态不成熟（grammar 动态库打包、平台矩阵） | 证据盘点期先做 spike；Roslyn（C#）作为高质量回退/增强路径 |
| 调用消解精度不足导致 impact 噪声 | confidence 分级 + 抽样验证门禁；先高置信主干后 fuzzy |
| 图膨胀（ACCESSES 边量大） | 预算控制 + 可配置开关，延续 relation-generation 优化经验 |
| schema 变更破坏既有工具 | smoke 全量回归为每个 track 的收口门禁 |
