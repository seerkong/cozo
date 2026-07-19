# Mission：add-dotnet-llm-wiki-java-spring-semantic-parsing

## 背景和动机

当前 C# 版 `depa-wiki` 的语义索引已具备 Tree-sitter 原生 P/Invoke、CLI adapter、per-language extractor、调用消解、执行流提取和 CodeKnowledge v2 图 schema，但 grammar 只覆盖 C# 与 TypeScript/JavaScript。

因此 Java 仓库会出现以下事实缺口：

- `.java` 不在默认索引扩展中。
- `.java` 没有映射到 `java` language id。
- Native backend 没有 `tree-sitter-java` grammar。
- CLI backend 不声明 Java 支持。
- 没有 Java symbol/structural extractor。
- 没有 Java 调用站点提取与 Java/Spring 框架语义推导。

这直接限制了后续对 `/Users/kongweixian/java/ks-ep/is-asset-new` 的本体识别和业务规则定位。该仓库是 Spring 多模块项目，业务入口、服务边界、事务边界、事件处理器和 DTO/Entity 角色需要先进入代码知识图，再由后续本体分析消费。

## 目标

1. 让 C# 版 `depa-wiki` 以 Tree-sitter Java 为结构解析基线，和 C#/TS/JS 共用现有 parser/backend/indexing contract。
2. 把 Java 的 package、import、类型、成员、继承/实现、调用站点和结构边纳入 `ck_symbol` / `ck_edge`。
3. 复用现有调用解析设施，形成带 confidence、resolver、evidence 的 Java `CALLS` / `ACCESSES` / `EXTENDS` / `IMPLEMENTS` 等边。
4. 在原始 Java 图事实之上推导首批 Spring 语义：HTTP Controller/route、Service/Component、Repository/Mapper、Transactional、Listener/Handler、DTO/Entity/VO 角色。
5. 对真实 Java fixture 和 `is-asset-new` 做端到端验证，输出可定位、可解释、有界的图事实，为后续 IT 资产业务本体 mission 提供输入。

## 非目标

- 不在本 mission 中生成 `/Users/kongweixian/ai/solution/it-asset-ai-solution/cozo-ontology/v0` 业务本体制品。
- 不修改 `is-asset-new` 或 `is-asset-fe` 的业务代码、配置或 Git 历史。
- 不要求完整 Maven/Gradle 编译、企业依赖下载或全量 Java 类型检查作为索引成功前置。
- 不在首批支持所有 Java 框架和所有注解；Spring 语义词汇表保持有界。
- 不以正则解析替代 Tree-sitter；正则只作为单文件失败时的兼容降级。
- 不追求完整 Java compiler-grade semantic resolution；后续可另建可选 Java semantic enhancer track。

## 成功判据

- `RepositoryIndexRequest` 默认覆盖 `.java`，语言识别返回 `java`，既有语言行为无回归。
- Native Tree-sitter backend 在支持平台加载 Java grammar，并通过 ABI 检查；没有 native grammar 时有结构化诊断并按既有降级策略继续工作。
- Java fixture 能稳定产出 package/type/member/import/extends/implements/contains 事实及行号、`sym_key`、`parent_id`、resolver。
- Java fixture 能产出可验证的调用站点和高置信 `CALLS` 边；无法解析的调用不会被静默乱连。
- Spring fixture 能产出 endpoint、service、repository、transactional、listener/handler 等派生事实，并保留注解和源码位置证据。
- 对 `is-asset-new` 的索引能够识别 Java 符号和 Spring 入口，而不是 0 symbols；结果统计和抽样查询可复现。
- C#/TS/JS 的既有测试、索引行为和工具结果保持兼容。
- 所有实现、测试和行为变化由后续真实 Codument tracks 承担；mission 仅负责编排和验证。

## 为什么需要 mission 而不是单个 track

目标跨越 native grammar 构建、语言 extractor、调用消解、Spring 派生语义和真实仓库质量验证。它们有明确依赖关系，但每个阶段都有独立的技术失败模式和验收门禁；执行中还可能需要根据 grammar ABI、Java 语法覆盖率、框架注解变体和调用消解质量重新切片。因此由 mission 管理 DAG 和受控重规划，具体代码/测试/behavior 由真实 track 落地。

## 产出边界

实现完成后，本 mission 预期提供：

- Java 代码图事实与查询能力；
- Spring 派生事实及其 provenance；
- 对真实 Java 仓库的索引质量报告；
- 后续 IT 资产本体抽取所需的 Java 后端证据入口。

业务本体本身应由后续 mission 输出到目标 solution 仓库，避免把 parser implementation knowledge 与领域 modeling 混在同一交付物中。
