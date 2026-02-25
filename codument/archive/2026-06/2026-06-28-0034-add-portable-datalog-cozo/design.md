# 方案设计：Portable Datalog 与 CozoScript 编译层

## 上下文

CozoScript 本身是 Datalog dialect，但它包含 Cozo 专有语法、stored relation 引用、参数形式与命令语义。给 agent 直接暴露 CozoScript 会导致两个问题：一是 agent 需要学习方言细节；二是安全边界难以集中治理。

本设计把语言层拆成两个 capsule：

- `Datalog.Core`：`cozo-lib-dotnet/packages/Datalog.Core/` 下的独立 csproj，纯语言前端和 backend-neutral IR，不依赖 Cozo，不依赖 Om。
- `Datalog.Cozo`：`cozo-lib-dotnet/packages/Datalog.Cozo/` 下的独立 csproj，Cozo backend adapter，将 normalized IR 编译为 CozoScript。

## 方案概览

1. **Datalog.Core**
   - 命名空间建议：`Cozo.DotNet.Datalog`
   - 目录：`cozo-lib-dotnet/packages/Datalog.Core/`
   - 项目：`Cozo.DotNet.Datalog.Core.csproj`
   - 职责：
     - Portable Datalog AST；
     - hand-written recursive descent parser 或小型 tokenizer + parser；
     - validator；
     - normalized IR；
     - diagnostics。
   - 不允许引用：
     - `CozoDb`；
     - `Om.Core`；
     - Cozo-specific relation metadata。

2. **Portable Datalog v1 profile**
   - 支持：
     - relation atom：`calls(X, Y)`；
     - rule：`reachable(X, Z) :- reachable(X, Y), calls(Y, Z).`；
     - query：`?- reachable($symbolId, Target).`；
     - string / number / bool / null constants；
     - parameter term：`$name`；
     - equality and comparisons；
     - conjunction；
     - stratified negation 可作为设计目标，但实现可先以 capability flag 控制。
   - 暂不支持：
     - Prolog cut；
     - arbitrary function calls；
     - unbounded backend-specific commands；
     - update / insert / delete；
     - aggregates，除非后续以明确 profile 增量加入。

3. **Validator**
   - 对每条规则检查：
     - head variables 必须在 positive body atom 中绑定；
     - negated atom/comparison 中变量安全；
     - query projection 变量安全；
     - predicate name 合法；
     - v1 profile 不支持的 construct 返回 diagnostic。
   - Validator 不判断 Cozo stored relation 是否真实存在；这个职责留给 backend mapping 或执行层。

4. **Normalized IR**
   - 目标不是复刻源文本，而是给 backend 编译器稳定消费。
   - 建议形态：
     - `DatalogProgramIr`
     - `DatalogRuleIr`
     - `DatalogLiteralIr`
     - `DatalogTermIr`
     - `DatalogQueryIr`
   - IR 保留 source span，方便 diagnostics 和 agent 反馈。

5. **Datalog.Cozo**
   - 命名空间建议：`Cozo.DotNet.Datalog.Cozo`
   - 目录：`cozo-lib-dotnet/packages/Datalog.Cozo/`
   - 项目：`Cozo.DotNet.Datalog.Cozo.csproj`
   - 输入：
     - normalized IR；
     - `CozoDatalogCompileOptions`；
     - `RelationMapping`。
   - 输出：
     - `CozoScriptCompileResult`，包含 `Script`、`Parameters`、`Diagnostics`。
   - 依赖：
     - `ProjectReference` 到 `../Datalog.Core/Cozo.DotNet.Datalog.Core.csproj`；
     - 不引用 `Cozo.DotNet.csproj`；
     - 不引用 `Om.Core`。
   - 编译策略：
     - inline rule 编译为 `rule_name[...] := ...`；
     - query 编译为 `?[...] := ...`；
     - stored relation atom 通过 mapping 编译为 `*relation{field: var}` 或 `*relation[var1, var2]`；
     - parameter term 编译为 Cozo 参数引用，不内联用户值；
     - string literals 必须统一 escaping。

6. **测试策略**
   - Datalog.Core：
     - lexer/parser fixture；
     - validator negative cases；
     - normalized IR shape tests。
   - Datalog.Cozo：
     - compiler snapshot tests；
     - escaping/parameter tests；
     - relation mapping tests；
     - optional in-memory Cozo execution smoke。

## 影响范围与修改点（Impact）

- `cozo-lib-dotnet/packages/Datalog.Core/**`：新增纯语言层独立项目。
- `cozo-lib-dotnet/packages/Datalog.Cozo/**`：新增 Cozo backend 编译层独立项目。
- `cozo-lib-dotnet/tests/**`：新增 parser/validator/compiler 测试。
- `cozo-lib-dotnet/tests/Cozo.DotNet.Om.Tests.csproj`：增加对两个 package project 的 test-time 引用。

## 决策摘要

- Portable Datalog parser/validator 是 `packages/Datalog.Core` 独立 csproj，不依赖 Cozo，不依赖 Om。
- CozoScript 编译器独立放在 `packages/Datalog.Cozo` 独立 csproj，可以依赖 `Datalog.Core`，但不依赖 `Cozo.DotNet.csproj` 或 `Om.Core`。
- NamedQuery、agent facade、wiki compiler 不进入本 track；它们由后续 `Om.Query` / `Om.CodeKnowledge` track 承接。
- v1 选择受限 Datalog 子集，优先覆盖递归查询、影响分析和约束检查，而不是追求全语言兼容。

## 风险 / 权衡

- **Datalog 方言边界容易膨胀**：v1 必须用 capability checks 明确拒绝未实现 construct。
- **Cozo stored relation 语法有多种形态**：先以 relation mapping 描述字段映射，避免 parser 里出现 Cozo 专有语法。
- **AI 生成查询可能成本过高**：validator 只能做静态安全检查，后续执行层仍需要 timeout、row limit、recursion policy。
- **过早绑定 Om 类型系统会污染底层包**：本 track 明确禁止 Datalog.Core 依赖 Om.Core。

## 待解决问题

- stratified negation 是否第一版即完整支持，还是先以 AST/diagnostic 识别但默认关闭。
- relation mapping 第一版是否只支持 named fields，还是同时支持 positional stored relations。
- 是否后续将 Datalog.Core / Datalog.Cozo 独立发布为 NuGet 包，还是先作为 repository-local package projects 被上层项目引用。
