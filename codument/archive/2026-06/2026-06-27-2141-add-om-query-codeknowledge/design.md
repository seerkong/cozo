# 方案设计：Om.Query 与 Om.CodeKnowledge

## 上下文

当前 `.NET OM` 已具备可组合基础：

- `Om.Core`：本体事实源、`ICozoOmStore`、schema/type/entity/relation/constraint/action/schema version/existential/permission。
- `Om.Batch`：事务化批量写入与 touched entity validation。
- `Om.Analytics`：template result envelope、graph/tree/ranking visual models。
- `Datalog.Core` / `Datalog.Cozo`：Portable Datalog AST/validator/IR 与 CozoScript compiler。

本 track 在这些能力之上新增查询产品层和代码/文档知识图谱领域层。

## 方案概览

1. **Om.Query**
   - 位置：`cozo-lib-dotnet/src/Om.Query/`
   - 依赖：
     - `Om.Core`：使用 `CozoOm.Runtime.Store` 执行查询。
     - `Datalog.Core`：解析与校验 Portable Datalog。
     - `Datalog.Cozo`：编译为 CozoScript。
   - 主要类型：
     - `NamedQueryDefinition`
     - `NamedQueryParameter`
     - `OmQuerySafetyPolicy`
     - `OmQueryRegistry`
     - `OmQueryEngine`
     - `OmQueryExecutionResult`
     - `OmQueryResultShape`
   - 执行流程：
     1. 根据 NamedQuery 或 ad-hoc source 获得 Portable Datalog source。
     2. 用 `Datalog.Core` parse + validate。
     3. 用 `Datalog.Cozo` 按 relation mapping 编译 CozoScript。
     4. 通过 `ICozoOmStore.RunAsync(... immutable: true)` 执行。
     5. 按 result shape 映射 table/graph/domain payload。

2. **Om.CodeKnowledge**
   - 位置：`cozo-lib-dotnet/src/Om.CodeKnowledge/`
   - 依赖：
     - `Om.Core`
     - `Om.Query`
   - 不依赖：
     - MCP/server/HTTP
   - schema relations：
     - `ck_repo`
     - `ck_file`
     - `ck_symbol`
     - `ck_relation`
     - `ck_doc_block`
     - `ck_concept`
     - `ck_diagnostic`
     - `ck_owner`
     - `ck_wiki_page`
   - batch input：
     - repository facts
     - file facts
     - symbol facts
     - relation facts：calls/imports/references/defines/documents/mentions/owns
     - doc block facts
   - agent facade：
     - 返回 C# domain records，而不是 HTTP/MCP envelope。
     - 内部优先走 `Om.Query` NamedQuery，少量简单聚合可以直接走 store。

3. **默认 NamedQuery**
   - `code.symbolContext`
   - `code.impactOfChange`
   - `code.docsForCode`
   - `code.traceConcept`
   - `code.explainRelation`

4. **wiki plan**
   - 第一版只生成结构化 plan：
     - pages
     - source files
     - symbols
     - doc blocks
     - missing docs
     - stale docs
   - 不直接写 Markdown 文件。

## 影响范围与修改点（Impact）

- `cozo-lib-dotnet/src/Om.Query/**` 新增查询层。
- `cozo-lib-dotnet/src/Om.CodeKnowledge/**` 新增代码/文档知识图谱领域层。
- `cozo-lib-dotnet/Cozo.DotNet.csproj` 增加对 `Datalog.Core` / `Datalog.Cozo` 的项目引用。
- `cozo-lib-dotnet/tests/Program.cs` 增加集成测试。

## 决策摘要

- `Om.Query` 不是纯语言层；它是 OM 查询产品层，可以依赖 `Om.Core` 和 Datalog packages。
- `Om.CodeKnowledge` 是领域 capsule，依赖 `Om.Query`，不反向污染 `Om.Core`。
- 第一版以 NamedQuery 为默认 agent 入口，同时保留 ad-hoc Portable Datalog 高级入口。
- 第一版 code knowledge indexing 由调用方传入 facts，不内置语言解析器和 Git history crawler。
- wiki compiler 第一版返回 plan，不写文件。

## 风险 / 权衡

- **Portable Datalog profile 仍然较小**：默认 NamedQuery 应优先覆盖高价值查询，减少 agent 现写复杂规则。
- **Cozo relation mapping 易错**：CodeKnowledge 应内置稳定 mapping，外部调用方只传参数。
- **scope 可能膨胀**：MCP/server、embedding、Git history crawler 全部延后。
- **结果形状需要稳定**：复用 `Om.Analytics` 的 envelope 思路，避免每个 facade 临时拼匿名对象。

## 待解决问题

- 后续是否把 `Om.Query` 拆为独立 csproj/package。
- 后续是否将 `Om.CodeKnowledge` 与 MCP server 分离成两个 track。
- 后续是否接入 full-text/vector search。
