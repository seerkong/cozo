# Design

## CLI Shape

```bash
cozo-wiki tools [storage-options]
cozo-wiki call <tool-name> [tool-args] [storage-options]
```

`call` 支持两种参数输入：

- `--arguments-json '{"query":"SampleService","limit":5}'`
- `--query SampleService --limit 5`

CLI 参数使用 kebab-case，转换为 MCP tool 的 camelCase 参数。例如 `--repo-path` 转为 `repoPath`。

## Shared Tool Runner

把 MCP tool 执行逻辑从 `LlmWikiMcpServer` 提取到 `LlmWikiToolRunner`：

- MCP `tools/call` 调用 runner。
- CLI `call` 调用同一个 runner。
- CLI `tools` 输出同一个 `ToolsJson()`。

这样 CLI 和 MCP 不会出现行为分叉。

## Storage

CLI `tools` 和 `call` 使用与 `mcp/index/wiki` 相同的 `CozoWikiStorageOptions`：

- 默认 sqlite。
- 默认 DB 位于 `<work-dir>/.cozo-wiki/cozo-wiki.db`。
- `--db` 可覆盖。

## Output

CLI 输出直接序列化 tool result JSON，不包 MCP `content` envelope，便于 shell 和 E2E 断言。
