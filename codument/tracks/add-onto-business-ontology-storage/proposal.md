# 变更：新增 `onto_*` 业务本体存储层

## 背景和动机

现有 `.depa-wiki` 数据库保存代码观察 `ck_*` 和可选的 DEPA 架构判断 `depa_*`。它们都不是业务本体：前者描述实现，后者描述架构。当前 XML exporter 只读取 `depa_*`，因此不能承载资产管理领域的类型、关系、规则和生命周期。

本 track 新增 LlmWiki-owned 的 `onto_*` generation state，使后续语义投影能先把业务解释及其来源写入数据库，再由单独的 XML track 导出受 DSL 约束的本体 bundle。

## 目标

- 建立独立且可初始化的 `onto_*` relation family。
- 提供 C# models 和 store API，能保存概念、属性、关系、规则、生命周期、状态、迁移、映射、证据、候选与审查。
- 强制 repository-relative evidence、有限 status/grade/confidence、直接证据引用和 exportable eligibility。
- 支持可重复 generation 写入和按 ontology ID 的安全替换。
- 用内存 Cozo fixture 覆盖隔离、完整性、候选审查和刷新语义。

## 非目标

- 不从 `ck_*` 自动推导业务概念；这属于后续 derivation track。
- 不写 XML、CLI command 或变更现有 `ontology-xml` 兼容语义。
- 不读取、依赖、转换或关联 `depa_*`。
- 不修改 `cozo-lib-bun/`、`cozo-lib-dotnet/src/Om.Core/`、`cozo-lib-dotnet/tests/`。

## 变更内容

- 在 `Cozo.DotNet.LlmWiki.Tools` 增加业务本体 models、schema/store 和导出前读取 view。
- 在 LlmWiki tests 增加 memory-db fixture，覆盖 `onto_*` 的完整 graph、拒绝路径和 scoped refresh。
- 增加 `dotnet-llm-wiki` 的行为增量，定义该存储层的可验证合同。

## 影响范围

- 受影响行为：`dotnet-llm-wiki/business-ontology-storage`。
- 受影响代码：`cozo-lib-dotnet-llm-wiki/packages/Cozo.DotNet.LlmWiki.Tools/**`、`cozo-lib-dotnet-llm-wiki/tests/**`。
- 兼容性：additive；现有 CodeKnowledge、DEPA snapshot、wiki 和 CLI 行为不变。
