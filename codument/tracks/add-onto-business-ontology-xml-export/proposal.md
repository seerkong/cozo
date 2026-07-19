# 变更：导出 `onto_*` 业务本体 XML

## 目标

从 `BusinessOntologyStore` 的 exportable view 生成符合 `ontology-xml-dsl` 的中文模块化 XML bundle，并提供显式、可重放的 CLI derive/export 操作。

## 边界

- 新格式名为 `business-ontology-xml`；既有 `runtime-snapshot`、`ontology-xml`、`wiki` 保持 DEPA 合同。
- Canonical XML 只包含 accepted/hypothesis 语义对象与实现映射、evidence；pending/rejected candidate 写入审计 JSON，不冒充本体声明。
- XML 不包含 `depa_*`，不读取 DEPA judgments。

## 验收

- memory fixture 输出 root、types、relations/rules/lifecycles（存在时）、mappings、evidence 与 candidate audit，所有解释为中文。
- `ontology-xml-dsl --generated` 通过，hypothesis 有 inferred evidence。
- CLI 能显式 derive 一个 ontology generation，并导出 `business-ontology-xml`；旧 DEPA format 测试不变。
