---
knowledge_plane: domain
doc_role: reference
status: active
context: code-knowledge
last_verified: 2026-07-06
---

# Code Knowledge Code Map

| Area | Source |
|------|--------|
| v2 schema 定义与迁移语义 | `cozo-lib-dotnet/src/Om.CodeKnowledge/CodeKnowledgeSchema.cs` |
| 公共 API（Init/Index/查询/RemoveFileFacts） | `cozo-lib-dotnet/src/Om.CodeKnowledge/CozoOmCodeKnowledgeExtensions.cs` |
| 事实/批量模型（CodeSymbolFact/CodeEdgeFact 等） | `cozo-lib-dotnet/src/Om.CodeKnowledge/CodeKnowledgeModels.cs` |
| 图投影（call_graph/import_graph/cluster_input） | `cozo-lib-dotnet/src/Om.CodeKnowledge/CodeGraphProjections.cs` |
| 执行流抽取（ck_process/ck_entry_point 物化） | `cozo-lib-dotnet/src/Om.CodeKnowledge/ProcessExtraction.cs` |
| 深度影响分析 | `cozo-lib-dotnet/src/Om.CodeKnowledge/DeepImpact.cs` |
| capsule 级 README（API 与派生层语义速览） | `cozo-lib-dotnet/src/Om.CodeKnowledge/README.md` |
| 索引管线（tree-sitter 主路径 + regex 兜底） | `cozo-lib-dotnet-llm-wiki/packages/Cozo.DotNet.LlmWiki.Indexing/RepositoryIndexer.cs` |
| 调用解析与置信度分档 | `cozo-lib-dotnet-llm-wiki/packages/Cozo.DotNet.LlmWiki.Indexing/CallResolver.cs` |
| Roslyn 语义增强与边合并 | `cozo-lib-dotnet-llm-wiki/packages/Cozo.DotNet.LlmWiki.Indexing/RoslynMergeStep.cs` |
| 增量索引文件级 diff | `cozo-lib-dotnet-llm-wiki/packages/Cozo.DotNet.LlmWiki.Indexing/IncrementalDiff.cs` |
| 语法级入口点候选（http_route/mcp_tool） | `cozo-lib-dotnet-llm-wiki/packages/Cozo.DotNet.LlmWiki.Indexing/EntryPointCandidates.cs` |
| .NET 侧测试 | `cozo-lib-dotnet/tests/Program.cs` |
| 索引/增量/一致性测试 | `cozo-lib-dotnet-llm-wiki/tests/Cozo.DotNet.LlmWiki.Tests/` |
