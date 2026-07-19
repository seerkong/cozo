# Mission: add-onto-business-ontology-projection

## 背景和动机

`depa-wiki` 已能把 Java/Spring、C#、TypeScript/JavaScript 等代码解析为 `ck_*` 代码知识事实，并可生成 `depa_*` 架构判断。现有 `DepaOntologyExporter` 只将持久化的 `depa_*` OM 行投影为 XML，因此其产物本质是 DEPA runtime snapshot，而不是用户希望获得的业务本体。

本 mission 要新增独立的 `onto_*` 持久化语义层：从已索引的代码事实和源码锚点形成可审计的业务概念、属性、关系、规则与生命周期，再导出符合既有 XML DSL 的业务本体。`depa_*` 与 `onto_*` 可以在未来互相标注，但本阶段不建立二者的运行时依赖或映射功能。

## 目标

1. 定义并持久化独立的 `onto_*` 业务本体元模型，覆盖概念、属性、关系、规则、生命周期/状态/迁移、实现映射与证据。
2. 从 `.depa-wiki` 已索引的代码事实、框架派生事实和源码锚点建立有界、可解释的业务候选与语义投影；不将 `ck_*`、`depa_*` 当作导出业务对象。
3. 提供读取已持久化 `onto_*` 事实并导出标准业务本体 XML 的 CLI/API 路径，产物采用业务命名空间和中文解释文本。
4. 对 `is-asset-new` 与 `is-asset-fe` 的既有索引数据库进行端到端验证，确认导出包含真实业务类型、属性、关系、规则和生命周期，而非空壳或 DEPA 图。
5. 保留可复核的源码证据与置信度，并为未来 `onto_*`/`depa_*` 互相标注保留非侵入扩展空间。

## 非目标

- 不修改 `is-asset-new`、`is-asset-fe` 的源码、配置或 Git 历史。
- 不把 `depa_*` 转换成 `onto_*`，也不要求任何一方先于另一方生成。
- 不在本 mission 中实现 `onto_*` 与 `depa_*` 的双向映射、同步或可视化。
- 不把 LLM 推断、命名启发式或框架约定直接提升为无证据的业务真相。
- 不修改 `cozo-lib-bun/`、`cozo-lib-dotnet/src/Om.Core/` 或其测试；如发现通用内核缺口，必须受控重规划并与 OM parity mission 协调。
- 不破坏既有 `runtime-snapshot` 的审计语义；旧 `ontology-xml` 的兼容迁移由专门 export track 先设计、测试再执行。

## 成功判据

- 数据库中存在由 LlmWiki 层拥有的 `onto_*` 事实，且可在不读取 `depa_*` 实体的条件下被查询和导出。
- 每个已确认业务概念/属性/关系/规则/生命周期项都含稳定 identity、中文说明、来源/置信度与至少一个源码或索引证据锚点。
- 导出的 XML 只表达业务本体概念；`ck_*`、`depa_*` 不作为 XML 类型、关系、规则或生命周期节点出现。
- 语义投影能清楚区分候选、确认和需要审查的结论；重复执行在相同索引输入下结果稳定，并不会累积陈旧事实。
- XML 通过既有 ontology XML DSL 的结构校验，并能在两个真实项目数据库上生成可检查的业务本体输出。
- 新增/调整的 CLI、存储、投影和导出行为具有聚焦单元测试、fixture 测试与真实数据库端到端验证。

## 为什么需要 mission 而不是单个 track

该目标跨越持久化元模型、事实到业务语义的提升、CLI/export 兼容迁移、两仓库真实数据验证和可能的人工审查门禁。每一步都有不同的失败模式，并且 export 格式兼容和语义提升策略必须在已有 evidence 上受控收敛。因此 mission 负责编排依赖、观察实际输出和必要的重规划；真实代码、测试和行为变更都由下属 tracks 完成。

## 交付边界

- 实现归属：`cozo-lib-dotnet-llm-wiki/**` 及相应 Codument tracks/behaviors。
- 验证输入：`/Users/kongweixian/java/ks-ep/is-asset-new/.depa-wiki/` 与 `/Users/kongweixian/nodejs/ks-ep/is-asset-fe/.depa-wiki/` 的已索引数据库。
- 业务 XML 交付目录：`/Users/kongweixian/ai/solution/it-asset-ai-solution/cozo-ontology/v0/ontology/`；它是命令生成的制品目标，不是本 mission 的实现真源。
