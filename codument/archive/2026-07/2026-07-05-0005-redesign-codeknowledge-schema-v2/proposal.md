# 变更：CodeKnowledge v2 图 schema 重设计与迁移

## 背景和动机 (Context And Why)

cozo-wiki 当前的代码知识图 schema（Om.CodeKnowledge 的 9 张 ck_* relations）只有通用 ck_relation（无 confidence、无 resolver 来源），无法承载真实调用图（CALLS/EXTENDS/ACCESSES）、社群检测与执行流。这是 mission `deepen-llm-wiki-code-graph`（P0 语义图地基）的第一块拼图：先把 v2 schema 与迁移立起来，让后续 tree-sitter ingestion（G3）、调用消解（G4）、社群/执行流（G5）有落点，同时保持既有 12 个 MCP 工具存活。

设计定稿依据：mission 的 analysis/schema-v2-design.md（spike 证据：Roslyn DocId 作 canonical 符号 ID、sym_key=(lang,全限定名,arity) 归一键、confidence 分级 1.0/0.6/0.3；Cozo 固定规则 louvain/SCC/最短路要求 [from,to] 边投影）。本 track 的 design.md 内嵌该定稿以自包含。

## "要做"和"不做" (Goals / Non-Goals)

**目标:**
- v2 relations：ck_meta（schema_version）、扩展 ck_symbol（sym_key/doc_id/parent_id/lang/visibility/exported/resolver）、新 ck_edge（from,to,kind,file,line => confidence,resolver,evidence）、ck_entry_point、ck_community/ck_member、ck_process/ck_process_step。
- 重建式迁移：InitCodeKnowledgeAsync 校验 schema_version；检测到 v1 旧表时报带指引错误；提供 reindex 路径（drop+recreate）。
- 索引写入路径与 12 个既有工具适配：ck_relation 由 ck_edge 取代（regex 索引器先喂新表，confidence=0.3/resolver=regex），symbol_context/impact_of_change/explain_relation/overview_graph 等查询改读 ck_edge。
- Datalog 投影规则常量（call_graph/import_graph/cluster_input），为固定规则算法与 P1 工具预留。

**非目标:**
- 不引入 tree-sitter/Roslyn 解析（G3/G4 track）。
- 不实现社群检测/执行流计算（G5 track；本 track 只建表）。
- 不做 v1 数据搬迁（索引可重算）。

## 变更内容（What Changes）

- **BREAKING**：ck_relation 废弃，由 ck_edge 取代；ck_symbol 列扩展。旧库需 reindex（工具报错带指引 + reindex 选项）。
- Om.CodeKnowledge：CodeKnowledgeSchema v2、CodeKnowledgeModels 扩展（CodeEdgeFact/CodeEntryPointFact 等）、CozoOmCodeKnowledgeExtensions 查询适配。
- llm-wiki：RepositoryIndexer 写 ck_edge；LlmWikiToolRunner/Server 相关查询适配。

## 影响范围（Impact）

- 受影响能力：cozo-dotnet-codeknowledge（新 capability）、llm-wiki-tools（行为不变，实现适配）。
- 受影响代码：cozo-lib-dotnet/src/Om.CodeKnowledge/*、cozo-lib-dotnet-llm-wiki/packages/{Indexing,Tools,Server}/*、两侧 tests。
