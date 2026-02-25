# 变更：混合检索（Cozo FTS + 向量 RRF）

## 背景和动机 (Context And Why)

mission `expand-llm-wiki-agent-tools`（P1）G2。cozo-wiki 的 semantic_search 目前纯向量（HNSW + MiniLM/确定性降级）。GitNexus 是 BM25 FTS + 向量的 RRF 融合（K=60），对精确标识符查询（函数名、错误串）文本检索显著优于纯向量。CozoDB 内置 FTS（`::fts create`，Simple/Ngram tokenizer + BM25 打分 `bind_score`）——零新依赖即可补齐，属差异化优势（GitNexus 需自维护 FTS 索引层）。

## "要做"和"不做" (Goals / Non-Goals)

**目标:**
- FTS 索引：ck_symbol（name+signature 合成文本）与 ck_doc_block（text）建 Cozo FTS 索引；中文注释检索用样例实测选 tokenizer（Simple 不命中则 Ngram）。
- 文本检索路径：BM25 top-k（bind_score）。
- RRF 融合：score = Σ 1/(K+rank)，K 默认 60；结果保留 sourceKind（code/docs）过滤语义。
- semantic_search 工具升级：mode=hybrid|vector|text（默认 hybrid）、rrfK 参数（add-only）；FTS 不可用/查询语法错误 → 降级 vector 并带诊断。
- 索引管线：FTS 索引创建幂等，随 InitCodeKnowledge/索引流程 ensure。

**非目标:**
- 不动 embedding provider 体系。
- 不做查询语法透传（用户输入按字面 escape，防 FTS 表达式注入）。
- 不做结果按 Process 聚合（后续与 context 深图化联动再说）。

## 变更内容（What Changes）

- VectorSearch capsule：FTS ensure/查询 + RRF 融合（改名义上升级为混合检索服务，命名空间不变）。
- Tools/Server：semantic_search 参数 add-only；smoke 扩展。

## 影响范围（Impact）

- 受影响能力：dotnet-llm-wiki-vector-search（新增 hybrid-search 需求）。
- 受影响代码：packages/{VectorSearch,Tools,Server}、tests。
