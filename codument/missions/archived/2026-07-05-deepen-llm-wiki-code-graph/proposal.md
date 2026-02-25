# Mission：deepen-llm-wiki-code-graph（P0 · 语义图地基）

## 背景和动机

cozo-wiki（cozo-lib-dotnet-llm-wiki）当前的代码知识图是"浅"的：`RepositoryIndexer` 用正则提取符号（C#/TS），关系只有 import/contains 级 + 文本相似的 doc-symbol 关联；tree-sitter 只以外部 CLI adapter 形式存在（`Cozo.DotNet.LlmWiki.SemanticParsing`，仅 parser_status/parse_file 诊断，解析结果未进图）。

对标 GitNexus（/Users/kongweixian/ai/src/GitNexus，2026-07-04 gap 分析）：

- GitNexus 有 14 阶段 ingestion DAG：tree-sitter 原生解析（14 语言，per-language extractor）→ 跨文件导入传播 → 类型解析（TypeEnv + 3 遍 fixpoint：构造推导、接收者类型、容器泛型）→ 统一 scope resolution（RFC #909 契约，registry-primary 调用解析，产出带 confidence 的 CALLS 边）→ MRO（METHOD_OVERRIDES/METHOD_IMPLEMENTS）→ Leiden 社群检测（Community 节点 + 内聚度）→ 执行流提取（框架检测入口点 → BFS → Process 节点 + STEP_IN_PROCESS 步骤边）。
- 节点类型 ~24 种（Function/Class/Method/Interface/Struct/Enum/Trait/…），边类型 ~18 种（CALLS/IMPORTS/EXTENDS/IMPLEMENTS/HAS_METHOD/ACCESSES/METHOD_OVERRIDES/MEMBER_OF/STEP_IN_PROCESS/…）。
- 关键参考文件：`gitnexus/src/core/ingestion/scope-resolution/`（统一 scope 解析契约）、`community-processor.ts`（Leiden）、`process-processor.ts`（执行流）、`type-resolution-system.md`（13 语言类型环境）。

cozo-wiki 没有 CALLS 边意味着：`impact_of_change` 只能给 import 级粗粒度影响；`trace`/`detect_changes` 类工具无从谈起；后续 P2 的 DEPA 健康度分析（effect 泄漏检测、runtime/config 归位、事实源分级）本质上是调用图 + 引用图上的可达性分析，同样无地基。

本 mission 是 P0——后续 P1（agent 工具补齐，mission `expand-llm-wiki-agent-tools`）、P2（DEPA + 分形 wiki，mission `add-llm-wiki-depa-fractal-wiki`）全部依赖本 mission 产出的深图。

## 目标（口径按 2026-07-04 已确认的 D1–D5 决策，见 decisions.md）

1. CodeKnowledge **v2 图 schema** 重设计 + 重建式迁移（参照 GitNexus 节点/边类型全集裁剪，边带 confidence），既有 12 个 MCP 工具适配且全量回归。
2. **混合解析 ingestion**：tree-sitter 原生 P/Invoke 统一管线（解析后端契约 + per-language extractor，首批 C# 与 TS/JS），解析结果落为图事实；现有 CLI adapter 降级为备选后端。
3. **调用消解**：导入绑定 → 跨文件符号匹配 → 接收者类型/构造推导，产出带 confidence 的 CALLS/EXTENDS/HAS_METHOD/ACCESSES 边；Roslyn 后端增强 C# 置信度（不追求 GitNexus 全量 fixpoint，先拿高置信主干）。
4. **社群检测**：CozoDB 内置 Louvain 固定规则（已查证 cozo-core/src/fixed_rule/algos/louvain.rs）→ Community + MEMBER_OF，聚类策略可替换。
5. **执行流提取**：入口点（public API 面 + Main/CLI + ASP.NET 路由/MCP 工具定义）→ 图遍历 → Process + STEP_IN_PROCESS，有界产出。

## 非目标

- 不做 PDG/污点分析（GitNexus 自己也在 M6 实验阶段）。
- 不追求 14 语言：首批 C# + TS/JS，架构上为多语言留 per-language extractor 接缝。
- 不在本 mission 中新增面向 agent 的 MCP 工具（trace/detect_changes 等归 P1 mission）。
- mission 本身不直接改代码；落地由 tracks 承担。

## 成功判据

- 对 cozo 仓库自身（cozo-lib-dotnet* 的 C# 代码 + viz 前端 TS 代码）索引后，图中存在 CALLS/EXTENDS/HAS_METHOD/ACCESSES 边，抽样验证方向与 confidence 合理。
- `symbol_context` / `impact_of_change` 基于调用图返回的结果可用（对比正则时代有实质提升，有 smoke 断言）。
- 社群检测与执行流在真实仓库上产出有界、可解释的 Community/Process 节点。
- 全部落地 track 归档，behavior delta 提升进 codument/behaviors/。

## 为什么是 mission 而不是单 track

跨解析器选型（存在不可逆技术选型）、图 schema 演进（涉及既有 ck_* relations 的兼容/迁移）、多个独立可验证的能力切片（解析 ingestion / 调用消解 / 聚类 / 执行流），单 track 无法安全闭环，需要证据盘点 → 设计收敛 → 分批落地。
