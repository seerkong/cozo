# 变更：新增 Om.Query 与 Om.CodeKnowledge

## 背景和动机 (Context And Why)

`add-dotnet-viz-server` 已经让 `.NET OM` 形成了可复用能力地图：`Om.Core` 负责本体事实源、事务与治理能力；`Om.Batch` 提供批量 ingestion；`Om.Analytics` 提供 graph/tree/ranking result envelope；`Datalog.Core` / `Datalog.Cozo` 提供 Portable Datalog 到 CozoScript 的语言底座。

现在需要把这些能力组合成面向 coding agent 的查询与代码/文档知识图谱层。目标不是复制一个简单 Graph Query API，而是让 agent 能通过稳定的 NamedQuery 调用和受控 Portable Datalog 查询，利用 CozoDB 的 Datalog 能力查询代码、文档、符号、调用/引用关系，并生成 wiki plan。

## "要做"和"不做" (Goals / Non-Goals)

**目标:**
- 新增 `cozo-lib-dotnet/src/Om.Query/`：
  - NamedQuery registry；
  - Portable Datalog query execution；
  - relation mapping 到 OM/CodeKnowledge stored relations；
  - result mapping：table / graph / path / ranking / domain object；
  - query safety policy：参数、limit、只读执行、backend diagnostics。
- 新增 `cozo-lib-dotnet/src/Om.CodeKnowledge/`：
  - code/doc ontology 初始化；
  - code/doc batch indexing API；
  - agent query facade；
  - wiki plan compiler。
- 复用现有能力边界：
  - `Om.Core` 作为事实源和 `ICozoOmStore` 执行边界；
  - `Om.Batch` 的 ingestion 思路用于批量写入代码/文档事实；
  - `Om.Analytics` 的结果 envelope/graph/ranking 设计作为返回结构参考；
  - `Datalog.Core`/`Datalog.Cozo` 作为 Portable Datalog 编译底座。

**非目标:**
- 不实现 MCP/server/HTTP 层。
- 不把代码/文档知识图谱塞进 `Om.Core`。
- 不让 agent 执行无限制 raw CozoScript。
- 不实现完整 Git history 索引；第一版以 working tree / 外部传入 fact batch 为主。
- 不实现 embedding/vector search；第一版保留字段但不依赖向量能力。
- 不实现完整 wiki Markdown 写盘；第一版输出 wiki plan。

## 变更内容（What Changes）

- `cozo-lib-dotnet/src/Om.Query/`：
  - `NamedQueryDefinition`：名称、版本、参数 schema、Portable Datalog source、relation mappings、result shape、safety policy。
  - `OmQueryRegistry`：注册、查找、列出 NamedQuery。
  - `OmQueryEngine`：执行 NamedQuery 或 ad-hoc Portable Datalog query。
  - `OmQueryResult` 系列：table rows、graph visual、path/domain payload。
  - 内置 diagnostics：parser/validator/compiler/backend 执行错误统一返回。
- `cozo-lib-dotnet/src/Om.CodeKnowledge/`：
  - schema 初始化：repo、file、symbol、relation、doc block、concept、diagnostic、ownership、wiki page。
  - batch indexing：写入文件、符号、关系、文档块。
  - agent facade：
    - `FindSymbolContextAsync`
    - `ImpactOfChangeAsync`
    - `TraceConceptAsync`
    - `DocsForCodeAsync`
    - `ExplainRelationAsync`
    - `BuildWikiPlanAsync`
  - 默认 NamedQuery：symbol context、impact of change、docs for code、concept trace、relation explanation。

## 影响范围（Impact）

- 受影响的能力（behaviors）：`dotnet-om-query`、`dotnet-code-knowledge`
- 受影响的代码：
  - `cozo-lib-dotnet/src/Om.Query/**`（新增）
  - `cozo-lib-dotnet/src/Om.CodeKnowledge/**`（新增）
  - `cozo-lib-dotnet/Cozo.DotNet.csproj`（如需引用 `packages/Datalog.*`）
  - `cozo-lib-dotnet/tests/**`
  - `cozo-lib-dotnet/README.md` 或新增局部 README
