# 设计：业务本体 XML 导出

`BusinessOntologyXmlExporter` 读取 `BusinessOntologyStore.ReadExportableAsync`，以 `System.Xml.Linq` 按 DSL 模块顺序写出 root、types、relations、rules、lifecycles、mappings 和 evidence。模块仅在相应对象存在时出现；evidence 始终存在。候选与 review 作为 `generation/candidates.json` 审计输出，不进入 XML 语义图。

CLI 将 `ontology derive` 作为单独的写入命令，并通过 `export --format business-ontology-xml` 作为独立读取/导出命令。两者都需要显式 `--ontology-id`，不触发 DEPA scan。
