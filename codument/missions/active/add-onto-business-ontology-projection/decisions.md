# Decisions

## D1: 业务本体与 DEPA 的关系

- Status: decided
- Decision: `onto_*` 与 `depa_*` 独立生成、独立演进；二者不是上下游依赖关系。
- Rationale: `depa_*` 表达代码架构与实现判断，`onto_*` 表达业务概念、属性、关系、规则和生命周期。将前者直接投影为后者会把实现结构误认为业务结构。
- Evidence: 用户在本 mission 创建前确认“互相标注，不是依赖关系；生成前后顺序不确定”。
- Confidence: 1.0
- Reversibility: moderate
- Durable candidate: yes

## D2: 首版范围

- Status: decided
- Decision: 首版不实现 `onto_*` 与 `depa_*` 的互相标注。
- Rationale: 标注关系需要独立定义映射语义、置信度、审查与增量更新，不应阻塞业务本体的独立建模与导出。
- Evidence: 用户明确要求当前不开发互相标注功能。
- Confidence: 1.0
- Reversibility: easy
- Durable candidate: yes

## D3: 并行约束

- Status: decided
- Decision: 本 mission 与 `converge-dotnet-om-bun-capability-parity` 并行推进。
- Rationale: 现有 parity mission 的代码输出端口限于 `cozo-lib-bun/`、`cozo-lib-dotnet/src/Om.Core/` 和 OM 测试；本 mission 优先拥有 LlmWiki 存储、语义投影、CLI 与测试层。
- Evidence: `codument/missions/active/converge-dotnet-om-bun-capability-parity/mission.xml` Ports。
- Confidence: 0.95
- Reversibility: moderate
- Durable candidate: yes

## D4: 语义提升纪律

- Status: decided
- Decision: 代码索引事实只能作为候选与证据；被接受的业务概念必须写入 `onto_*` 并带可复核 evidence，不能把 `ck_*` 或 `depa_*` 行直接作为导出业务概念。
- Rationale: 保持业务语义与实现/架构观察的事实源边界。
- Evidence: 当前 `DepaOntologyExporter` 只筛选 `depa_*` OM 类型，不能满足业务本体目标；用户已明确排除该投影方式。
- Confidence: 1.0
- Reversibility: moderate
- Durable candidate: yes

## Open Decisions

- D5: 在 `add-onto-business-ontology-xml-export` track 中确认旧 `ontology-xml` 的兼容策略、命令名和弃用路径。
- D6: 在 `add-onto-business-ontology-derivation` track 中确认候选抽取、语义投影执行器与人工复核的可测试边界。
