# Om.CodeKnowledge

`Om.CodeKnowledge` 是基于 CozoDB 和 .NET OM 的代码/文档知识图谱 capsule。它复用 `Om.Query` 执行 NamedQuery，并把代码、文档、符号与边事实写入独立的 `ck_*` stored relations。

当前 schema 为 **v2**（track `redesign-codeknowledge-schema-v2`）。BREAKING：v1 的 `ck_relation` 已由 `ck_edge` 取代（key 为 `(from_id, to_id, kind, file_id, line)`，携带 `confidence`/`resolver`/`evidence`）；检测到 v1 旧表时 `InitCodeKnowledgeAsync` 报错并提示以 `reindex: true` 走 drop+recreate 重建（索引数据可重算，不做数据搬迁）。

## 事实关系（v2）

- `ck_meta`（`schema_version=2`、`indexed_at`）
- `ck_repo`
- `ck_file`
- `ck_symbol`（v2 扩展列：`parent_id`/`lang`/`visibility`/`exported`/`sym_key`/`doc_id`/`resolver`）
- `ck_edge`（取代 v1 `ck_relation`；kind 枚举见 `CodeEdgeKinds`）
- `ck_entry_point`、`ck_process`/`ck_process_step`（派生层专表，待后续 track 填充）
- `ck_community`/`ck_member`（社群检测派生层，由 `ComputeCommunitiesAsync` 填充）
- `ck_doc_block`
- `ck_concept`
- `ck_diagnostic`
- `ck_owner`
- `ck_wiki_page`

## API

- `InitCodeKnowledgeAsync`（含 `reindex` 重载）
- `IndexCodeKnowledgeAsync`（`Edges` 写 `ck_edge`；遗留 `Relations` 自动转换为 confidence=0.3/resolver=regex 的 `ck_edge` 行并归一 v1 kind）
- `FindSymbolContextAsync`
- `ImpactOfChangeAsync`（含 `minConfidence` 重载）
- `DocsForCodeAsync`
- `TraceConceptAsync`
- `ExplainRelationAsync`
- `BuildWikiPlanAsync`
- `CodeGraphProjections`（`call_graph`/`import_graph`/`cluster_input` Datalog 投影）
- 社群检测（track `add-llm-wiki-community-detection` 正式化）：
  - `ComputeCommunitiesAsync(CommunityDetectionOptions?)` — 对加权 CALLS+IMPORTS 投影跑 Cozo Louvain 固定规则，`:replace` 写 `ck_community`/`ck_member`，返回 `CommunityDetectionResult`。
  - 质量语义：`cohesion = 社群内边权 / (社群内边权 + 跨界边权)`（孤立社群 = 1.0）；`label` 取成员限定名（`sym_key` 去 lang 前缀与 arity 后缀）的最长公共点分前缀（≥1 段），退化为最高加权度成员名，社群间冲突加 `#n` 后缀。
  - 确定性：社群按（size 降序，最小成员 symbolId 升序）规范化编号 `community:001…`，成员排序；同一图双跑输出完全一致。
  - 预算：`MinCommunitySize`（默认 3）以下的小簇计入 `NoiseSymbols` 不落库；`MaxCommunities`（默认 `max(10, min(200, symbolCount/20))`）按 size 降序截断并计入 `TruncatedCommunities`。
  - 查询：`ListCommunitiesAsync()`、`GetCommunityMembersAsync(communityId)`、`FindSymbolCommunityAsync(symbolId)`。
  - `CommunityDetectionOptions.Algorithm` 为策略接缝，当前仅支持 `"louvain"`。

- trace 与 cycles（track `add-llm-wiki-trace-and-check` 正式化，取代原 internal `DetectImportCyclesAsync` 预览）：
  - `TraceCallPathAsync(fromSymbolId, toSymbolId, TraceOptions?)` — ShortestPathDijkstra over `call_graph`（跳数最短），`MinConfidence`（默认 0.7）过滤边、`MaxDepth`（默认 16）后过滤；逐 hop 携带该符号对最高 confidence 调用点（file:line）与符号名；无路径返回 `Found=false + Reason`，不抛异常。
  - `DetectCyclesAsync(CycleKind: Import|Calls|Both)` — SCC（size>1）；成员从最小 id 出发沿组件内边规范化旋转，环按（kind, 首成员）排序，输出确定。

## 边界

- 第一版由调用方传入 facts，不内置语言 parser、Git history crawler、MCP/server 或 embedding/vector search。
- wiki compiler 第一版返回结构化 `WikiPlan`，不直接写 Markdown 文件。
