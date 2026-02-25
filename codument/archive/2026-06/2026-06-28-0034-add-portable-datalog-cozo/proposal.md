# 变更：新增 Portable Datalog 与 CozoScript 编译层

## 背景和动机 (Context And Why)

当前 `.NET OM` 正在从 `src/Om/` 重构为 `src/Om.Core/`，并逐步形成 `Om.Batch`、`Om.Analytics` 等 sibling capsule。后续如果让 coding agent 通过 MCP 等入口直接查询 CozoDB，直接暴露 CozoScript 会要求模型临时学习 CozoScript 的方言细节，稳定性和安全边界都不理想。

本 track 的目标是在 `.NET` 包中新增一个更底层、与 OM 解耦的 Datalog 语言层，并以 `cozo-lib-dotnet/packages/` 下的独立 sub project 交付：`Datalog.Core` 负责 Portable Datalog 的 AST、parser、validator、normalized IR 与 diagnostics；`Datalog.Cozo` 负责把 normalized Portable Datalog 编译为 CozoScript。这样 agent 可以生成更通用的 Datalog 子集，而 Cozo 仍作为强大的 Datalog 执行后端。

## "要做"和"不做" (Goals / Non-Goals)

**目标:**
- 新增 `cozo-lib-dotnet/packages/Datalog.Core/Cozo.DotNet.Datalog.Core.csproj`，提供不依赖 Cozo、不依赖 Om 的 Portable Datalog AST、parser、validator、normalized IR、diagnostics。
- 新增 `cozo-lib-dotnet/packages/Datalog.Cozo/Cozo.DotNet.Datalog.Cozo.csproj`，提供 Portable Datalog IR 到 CozoScript 的编译、CozoScript escaping、parameter binding、relation mapping 与 capability checks。
- 定义 v1 Portable Datalog profile，使其足够表达递归闭包、影响分析、约束违例检测、代码/文档追踪等 agent 查询。
- 通过测试证明 Datalog.Core 可以独立解析/校验，Datalog.Cozo 可以将典型规则编译为可执行 CozoScript。
- 为后续 `Om.Query` / `Om.CodeKnowledge` 提供语言底座，但不在本 track 实现 OM 查询产品层。

**非目标:**
- 不实现 `Om.Query` 的 NamedQuery registry、query execution planning 或 result mapping。
- 不实现 `Om.CodeKnowledge` 的代码/文档知识图谱 ontology、索引器、agent facade 或 wiki compiler。
- 不实现完整 Souffle、Prolog、SPARQL、Cypher 或 GQL 兼容。
- 不修改 CozoDB Rust 内核查询求值器。
- 不把 Portable Datalog parser/validator 绑定到 `Om.Core` 类型系统。

## 变更内容（What Changes）

- 新增 `cozo-lib-dotnet/packages/Datalog.Core/` 独立 csproj：
  - Portable Datalog AST：program、rule、query、atom、term、literal、predicate、diagnostic。
  - Parser：支持规则、查询、变量、常量、参数引用、比较表达式、注释、基本错误恢复。
  - Validator：检查 unsafe variables、duplicate head names、unknown constructs、v1 capability limits、递归可接受性。
  - Normalized IR：把语法糖归一化为 backend-neutral 的 rule/query graph。
  - Diagnostics：包含 severity、code、message、span，便于 MCP/agent 返回结构化错误。
- 新增 `cozo-lib-dotnet/packages/Datalog.Cozo/` 独立 csproj：
  - IR 到 CozoScript compiler。
  - relation mapping：portable predicate name 到 Cozo stored relation 或 inline rule 名的映射。
  - CozoScript escaping 与参数绑定：禁止字符串拼接式注入。
  - capability checks：在编译前拒绝当前 backend 不支持的 Portable Datalog construct。
  - compiler result：`script` + `parameters` + diagnostics。
- 新增 tests：
  - parser golden tests；
  - validator safety tests；
  - CozoScript compiler snapshot tests；
  - 可选 Cozo execution smoke tests。

## 影响范围（Impact）

- 受影响的能力（behaviors）：`dotnet-portable-datalog`
- 受影响的代码：
  - `cozo-lib-dotnet/packages/Datalog.Core/**`（新增）
  - `cozo-lib-dotnet/packages/Datalog.Cozo/**`（新增）
  - `cozo-lib-dotnet/tests/**`
  - 可能更新 `cozo-lib-dotnet/Cozo.DotNet.csproj`
  - 可能更新 `cozo-lib-dotnet/README.md`
