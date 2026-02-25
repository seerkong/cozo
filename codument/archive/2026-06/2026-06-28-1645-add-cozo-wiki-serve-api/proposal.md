# 变更：为 cozo-wiki 添加本地 serve API

## 背景和动机 (Context And Why)

`cozo-lib-dotnet-llm-wiki-viz` 需要一个本地 HTTP API 来访问 repo DB、触发索引、语义搜索、wiki 构建、symbol context/impact 和 NamedQuery。当前 `cozo-wiki` 只有 MCP stdio 与 CLI call。G2 已抽出共享 tool runner，本 track 在其基础上添加 server package 和 CLI `--serve`。

## "要做"和"不做" (Goals / Non-Goals)

**目标:**

- 新增 `Cozo.DotNet.LlmWiki.Server` ASP.NET Core package。
- `cozo-wiki --serve` 启动本地 server；兼容 `cozo-wiki serve` alias。
- 默认 host `127.0.0.1`，默认 port `4176`。
- 暴露 health/status/tools/call/index/embeddings/search/symbol/wiki/query endpoints。
- 支持可选 `--static-dir` 和 `--api-only`。

**非目标:**

- 不实现鉴权、多用户或远程部署能力。
- 不开放 raw CozoScript 任意执行。
- 不实现前端 UI。

## 变更内容（What Changes）

- 新建 server csproj 和 `LlmWikiWebServer`。
- MCP server CLI 引用 server 包并增加 `--serve`/`serve` dispatch。
- README/help 增加 serve 参数说明。
- Tests 增加 endpoint smoke。

## 影响范围（Impact）

- 受影响的能力（behaviors）：`llm-wiki-server`
- 受影响的代码：
  - `cozo-lib-dotnet-llm-wiki/packages/Cozo.DotNet.LlmWiki.Server/**`
  - `cozo-lib-dotnet-llm-wiki/packages/Cozo.DotNet.LlmWiki.McpServer/**`
  - `cozo-lib-dotnet-llm-wiki/tests/**`
