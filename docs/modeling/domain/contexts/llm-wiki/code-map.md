---
knowledge_plane: domain
doc_role: reference
status: active
context: llm-wiki
last_verified: 2026-07-06
---

# LLM Wiki Code Map

| Area | Source |
|------|--------|
| 18 工具运行器（工具矩阵与分发） | `cozo-lib-dotnet-llm-wiki/packages/Cozo.DotNet.LlmWiki.Tools/LlmWikiToolRunner.cs` |
| 混合搜索（RRF 融合） | `cozo-lib-dotnet-llm-wiki/packages/Cozo.DotNet.LlmWiki.VectorSearch/CozoVectorSearchService.cs` |
| CLI/MCP 入口与子命令分发 | `cozo-lib-dotnet-llm-wiki/packages/Cozo.DotNet.LlmWiki.McpServer/Program.cs` |
| hooks 安装/卸载/状态 | `cozo-lib-dotnet-llm-wiki/packages/Cozo.DotNet.LlmWiki.McpServer/HookCommands.cs` |
| skills CLI（generate/clean/status） | `cozo-lib-dotnet-llm-wiki/packages/Cozo.DotNet.LlmWiki.McpServer/SkillsCommands.cs` |
| 双分形 wiki 管线 | `cozo-lib-dotnet-llm-wiki/packages/Cozo.DotNet.LlmWiki.Wiki/FractalWikiPipeline.cs` |
| 分形文档格式（frontmatter/职责块渲染） | `cozo-lib-dotnet-llm-wiki/packages/Cozo.DotNet.LlmWiki.Wiki/FractalDocFormat.cs` |
| 分形规则 checker | `cozo-lib-dotnet-llm-wiki/packages/Cozo.DotNet.LlmWiki.Wiki/FractalSpecChecker.cs` |
| skills 生成器 | `cozo-lib-dotnet-llm-wiki/packages/Cozo.DotNet.LlmWiki.Wiki/SkillsGenerator.cs` |
| legacy wiki 编译器 | `cozo-lib-dotnet-llm-wiki/packages/Cozo.DotNet.LlmWiki.Wiki/WikiCompiler.cs` |
| 语义解析后端选择（tree-sitter CLI） | `cozo-lib-dotnet-llm-wiki/packages/Cozo.DotNet.LlmWiki.SemanticParsing/ParserBackendSelector.cs` |
| git diff / detect_changes 支撑 | `cozo-lib-dotnet-llm-wiki/packages/Cozo.DotNet.LlmWiki.Core/GitDiff.cs` |
| 测试套件（工具/hook/skills/分形/eval） | `cozo-lib-dotnet-llm-wiki/tests/Cozo.DotNet.LlmWiki.Tests/` |
| eval 基准（任务集/评分/oracle 门禁） | `cozo-lib-dotnet-llm-wiki/eval/README.md` |
| 用户手册（P1，操作真源） | `cozo-lib-dotnet-llm-wiki/docs/index.md` |
