# 变更：cozo-wiki 直接 CLI tool 调用

## 背景和动机 (Context And Why)

当前 `cozo-wiki` 已能通过 MCP stdio 给 AI agent 暴露工具，但人工测试和 CLI E2E 测试需要额外构造 JSON-RPC MCP 客户端。用户希望可以直接在命令行中选择真实代码库的 `work-dir`，快捷执行 MCP tools，验证 repo DB、索引、查询、向量检索等功能是否正常。

## 目标

- 增加 `cozo-wiki tools`，输出可用工具列表。
- 增加 `cozo-wiki call <tool-name>`，直接调用 MCP tool 实现。
- 支持 `--arguments-json` 和常用 `--kebab-case` 参数转工具参数。
- 复用当前 sqlite/work-dir/global-dir 存储策略。
- README 增加 CLI 使用方式和 E2E 示例。

## 非目标

- 不实现交互式 shell。
- 不引入新的数据库格式。
- 不改变 MCP stdio 协议。
- 不为每个 tool 设计复杂专属子命令；MVP 使用统一 `call`。

## 影响范围

- `cozo-lib-dotnet-llm-wiki/packages/Cozo.DotNet.LlmWiki.McpServer/Program.cs`
- `cozo-lib-dotnet-llm-wiki/README.md`
- CLI/manual smoke 验证
