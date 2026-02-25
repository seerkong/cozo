# 变更：新增 Tree-sitter 语义解析 capsule

## 背景和动机 (Context And Why)

当前 LLM Wiki 的代码抽取是 regex/行级启发式，足够 MVP，但不能表达 Tree-sitter AST 级结构。用户要求引入语义解析，并暂时只支持 Tree-sitter。

## "要做"和"不做" (Goals / Non-Goals)

**目标:**
- 新增 `Cozo.DotNet.LlmWiki.SemanticParsing` project。
- 提供 Tree-sitter-only parser abstraction。
- 第一版实现 `tree-sitter` CLI adapter。
- 无 Tree-sitter CLI 时返回结构化 diagnostic。
- 在 MCP Server 暴露 `parse_file` / `parser_status` 工具。

**非目标:**
- 不引入 Roslyn。
- 不实现完整多语言 binding/call resolution。
- 不强制用户安装 Tree-sitter；缺失时不让整个 MCP server 启动失败。

## 变更内容（What Changes）

- 新增 SemanticParsing 包。
- McpServer 引用 SemanticParsing。
- MCP tools 增加：
  - `parser_status`
  - `parse_file`
- README 增加 Tree-sitter 使用说明。

## 影响范围（Impact）

- 受影响能力：`dotnet-llm-wiki-semantic-parsing`
- 受影响代码：
  - `cozo-lib-dotnet-llm-wiki/packages/Cozo.DotNet.LlmWiki.SemanticParsing/**`
  - `cozo-lib-dotnet-llm-wiki/packages/Cozo.DotNet.LlmWiki.McpServer/**`
  - `cozo-lib-dotnet-llm-wiki/tests/**`
