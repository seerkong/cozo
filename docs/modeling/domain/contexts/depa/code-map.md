---
knowledge_plane: domain
doc_role: reference
status: active
context: depa
last_verified: 2026-07-06
---

# DEPA Code Map

| Area | Source |
|------|--------|
| depa_* 本体 schema（类型/属性/关系定义） | `cozo-lib-dotnet/src/Om.Depa/DepaOntologySchema.cs` |
| 扫描管线（标注装载→检测→物化） | `cozo-lib-dotnet/src/Om.Depa/DepaScanPipeline.cs` |
| 检测器（V-* 规则实现） | `cozo-lib-dotnet/src/Om.Depa/DepaViolationDetectors.cs` |
| 报告查询（8 维分组/分母/占位规则） | `cozo-lib-dotnet/src/Om.Depa/DepaReportQueries.cs` |
| 报告模型 | `cozo-lib-dotnet/src/Om.Depa/DepaReportModels.cs` |
| 扫描模型 | `cozo-lib-dotnet/src/Om.Depa/DepaScanModels.cs` |
| 配置模型（depa-map 解析） | `cozo-lib-dotnet/src/Om.Depa/DepaConfigModels.cs` |
| 副作用 API 白名单目录 | `cozo-lib-dotnet/src/Om.Depa/DepaEffectCatalog.cs` |
| 公共 API（DepaScanAsync/GetConformanceReportAsync 等） | `cozo-lib-dotnet/src/Om.Depa/CozoOmDepaExtensions.cs` |
| 判据真源（规则映射，与实现双向机检） | `cozo-lib-dotnet/src/Om.Depa/rubrics/rule-map.md` |
| 判据真源（红灯目录/核查表/事实分级等） | `cozo-lib-dotnet/src/Om.Depa/rubrics/`（violation-catalog、capsule-protocol、component-protocol、fact-source-truth、fact-grade-classification、runtime-explicitness、depa-conformance） |
| 本仓标注样例 | `cozo-lib-dotnet/src/depa-map.json` |
| 检测器 fixture 测试与 rule-map 机检 | `cozo-lib-dotnet/tests/Program.cs` |
