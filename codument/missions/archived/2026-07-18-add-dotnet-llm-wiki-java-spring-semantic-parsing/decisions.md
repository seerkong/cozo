# Decisions

## D1 · Parser baseline

- Decision: 采用现有 Tree-sitter Native/CLI parser contract，新增 Java grammar 与 Java extractor。
- Rationale: 现有架构已经将 parser backend、per-language extractor、落图和调用解析分开；复用它可以保持 C#/TS/JS 行为不变，并使 Java 进入同一 `ck_symbol` / `ck_edge` schema。
- Evidence: `cozo-lib-dotnet-llm-wiki/packages/Cozo.DotNet.LlmWiki.SemanticParsing/`、`RepositoryIndexer`。
- Confidence: 0.98
- Reversibility: moderate
- Durable candidate: yes

## D2 · Java semantic enhancement

- Decision: 不把完整 Maven 编译或企业依赖可解析作为 Java 支持的硬前置；先实现 Tree-sitter 结构事实、轻量调用消解和注解驱动的 Spring 推导。
- Rationale: `is-asset-new` 包含企业内部依赖，完整 classpath 在索引环境中不稳定。结构事实应独立可用，后续可以增加可选 Java semantic enhancer。
- Evidence: 当前仓库的 Roslyn enhancement 采用可选增强并允许 baseline 保留；Java 仓库的 Maven parent/dependency 结构。
- Confidence: 0.9
- Reversibility: easy
- Durable candidate: yes

## D3 · Spring semantic scope

- Decision: 首批覆盖 Controller/HTTP route、Service/Component、Repository/Mapper、Transactional、Listener/Handler、DTO/Entity/VO role mapping。
- Rationale: 这些语义直接支撑执行流、业务入口、规则定位和前后端本体映射；暂不把所有 Spring/Kafka/MyBatis 注解纳入首批 vocabulary。
- Confidence: 0.88
- Reversibility: easy
- Durable candidate: no

## D4 · Mission boundary

- Decision: 本 mission 只交付 Java/Spring 代码知识图能力；IT 资产业务本体导出由后续 mission/track 消费该图谱。
- Rationale: parser infrastructure 与领域本体抽取的验收、事实源和失败模式不同，分离后更容易验证质量和重规划。
- Confidence: 0.95
- Reversibility: easy
- Durable candidate: yes
