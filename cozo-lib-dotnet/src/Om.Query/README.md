# Om.Query

`Om.Query` 是 .NET OM 的查询产品层。它不是纯语言层，也不是 HTTP/MCP 层；它把 Portable Datalog 查询编译为 CozoScript，并通过 `Om.Core` 的 `ICozoOmStore` 执行。

## 能力

- `NamedQueryDefinition`：名称、版本、Portable Datalog source、relation mapping、参数 schema、结果形状和安全策略。
- `OmQueryRegistry`：注册、查找、列出 NamedQuery。
- `OmQueryEngine`：执行 NamedQuery 或 ad-hoc Portable Datalog。
- `OmQueryResultShape`：当前支持 `Table`、`Graph`、`Raw`。
- diagnostics：parser、validator、compiler 错误会以结构化 diagnostics 返回，不执行 partial script。

## 边界

- 依赖 `Om.Core`、`Datalog.Core`、`Datalog.Cozo`。
- 不依赖 MCP、HTTP、server。
- 不提供无限制 raw CozoScript agent 入口。
