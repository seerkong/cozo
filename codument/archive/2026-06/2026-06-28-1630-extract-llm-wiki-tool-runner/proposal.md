# 变更：抽出 LLM wiki 共享工具执行层

## 背景和动机 (Context And Why)

`cozo-wiki` 当前把 CLI、MCP stdio server、storage option parsing、JSON helpers 和 `LlmWikiToolRunner` 都放在 `McpServer/Program.cs`。后续 `--serve` HTTP API 也需要调用同一套工具。如果不先抽出共享层，server 很容易复制 MCP/CLI 逻辑，造成工具 schema、参数绑定和结果行为漂移。

## "要做"和"不做" (Goals / Non-Goals)

**目标:**

- 新增 `Cozo.DotNet.LlmWiki.Tools` package。
- 将 `LlmWikiToolRunner`、tool metadata、storage options、CLI option to JSON argument helper、shared JSON options 抽到该包。
- 更新 `Cozo.DotNet.LlmWiki.McpServer` 复用新包。
- 保持现有 `mcp --stdio`、`tools`、`call`、`index`、`wiki` 行为兼容。

**非目标:**

- 不新增 HTTP server。
- 不新增前端。
- 不改变 tool 名称、参数 schema 或 storage 默认布局。
- 不改变 vector/search/wiki/indexing 的业务逻辑。

## 变更内容（What Changes）

- 新建 `cozo-lib-dotnet-llm-wiki/packages/Cozo.DotNet.LlmWiki.Tools/`。
- `McpServer` 改为引用 `Tools` 包。
- `Program.cs` 保留 CLI/MCP host 编排，移除内部重复工具实现。
- Solution 和测试项目加入新 package 引用。

## 影响范围（Impact）

- 受影响的能力（behaviors）：`llm-wiki-tools`
- 受影响的代码：
  - `cozo-lib-dotnet-llm-wiki/packages/Cozo.DotNet.LlmWiki.Tools/**`
  - `cozo-lib-dotnet-llm-wiki/packages/Cozo.DotNet.LlmWiki.McpServer/Program.cs`
  - `cozo-lib-dotnet-llm-wiki/Cozo.DotNet.LlmWiki.slnx`
  - `cozo-lib-dotnet-llm-wiki/tests/Cozo.DotNet.LlmWiki.Tests/**`
