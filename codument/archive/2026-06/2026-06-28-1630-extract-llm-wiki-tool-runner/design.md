# 方案设计：extract-llm-wiki-tool-runner

## 上下文

后续 server package 需要复用 MCP/CLI 当前的 tool runner。当前 runner 是 executable 内部类型，不可被 server 引用。

## 方案概览

1. 新增 `Cozo.DotNet.LlmWiki.Tools` library package。
2. 将以下类型迁入该包并设为 public：
   - `LlmWikiToolRunner`
   - `CozoWikiStorageOptions`
   - `LlmWikiCliOptions`
   - `LlmWikiJson`
3. `LlmWikiToolRunner` 继续依赖现有 indexing/wiki/parser/vector/query 能力。
4. `McpServer/Program.cs` 只保留 CLI dispatch、MCP JSON-RPC host 和 help 文案。
5. Tests 增加基本断言：tool metadata contains `semantic_search`；CLI option conversion 保持 kebab-case to camelCase。

## 影响范围与修改点（Impact）

- 新 package project and solution registration.
- MCP server project references tools package.
- Program.cs imports `Cozo.DotNet.LlmWiki.Tools`.

## 决策摘要

- 共享层放到独立 package，而不是 Core。Core 保持低层模型，不承载 Cozo/OM/tool execution 依赖。
- Storage options 一起抽出，因为 server 也需要复用同一 sqlite layout。

## 风险 / 权衡

- 迁移 internal 类型可能引入 namespace/visibility 编译错误；用 full solution build 验证。
- MCP stdout 必须保持干净，不能在 tools 包打印日志。
