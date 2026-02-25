# Mission：add-dotnet-llm-wiki

## 背景和动机

当前 `cozo-lib-dotnet` 已经具备 C# 版 OM、本体事实源、Portable Datalog、CozoScript 编译、`Om.Query` 与 `Om.CodeKnowledge`。这些能力已经能承载代码/文档知识图谱和 agent 查询，但还缺少类似 GitNexus 的产品化外壳：仓库扫描、代码/文档事实抽取、增量索引、wiki 生成，以及可被 coding agent 直接调用的 MCP stdio 入口。

GitNexus 的核心价值不是单一查询 API，而是把代码库离线索引成知识图谱，再通过 MCP/skills/wiki 把图查询能力喂给 coding agent。这个 mission 的目标是在当前 Cozo + .NET OM 能力基础上，形成一个本地、可运行、可继续演化的 C# MVP。

## 目标

- 对比 GitNexus-like 核心能力与当前 C# 能力，明确 gap。
- 在 `cozo-lib-dotnet-llm-wiki/packages/` 下创建多个独立 .NET project。
- 复用 `cozo-lib-dotnet` 的 `Om.CodeKnowledge` / `Om.Query` / `Datalog.*`，不重复建设图数据库内核。
- 实现仓库扫描、轻量符号/文档抽取、CodeKnowledge 批量索引、wiki Markdown 编译。
- 实现主入口项目，提供 stdio 模式 MCP 服务。
- 通过可执行 smoke test 验证索引、wiki、MCP 初始化/工具调用路径。

## 非目标

- 不复制 GitNexus 的完整 Tree-sitter、多语言 scope-resolution、PDG/taint、embedding/vector search。
- 不改 `cozo-lib-dotnet/src/Om.Core` 的事实源模型。
- 不开放无限制 raw CozoScript 作为默认 agent API。
- 不实现 HTTP bridge、Web UI、hooks 自动安装和多编辑器集成。
- 不实现跨仓库 group/contract bridge；保留未来扩展点。

## 为什么需要 mission 而不是单个 track

这个目标跨越证据盘点、架构切片、多个 package、CLI/MCP 主入口、测试验证和后续可演化路线。mission 用于保持期望态 DAG、记录 gap 证据、创建并执行首批落地 track；真实代码改动仍通过 track `add-dotnet-llm-wiki-mcp` 收敛。

## 成功判据

- mission 的 gap 分析明确列出“已有能力 / 缺失能力 / 首批实现边界”。
- track `add-dotnet-llm-wiki-mcp` 创建并完成。
- `cozo-lib-dotnet-llm-wiki/packages/` 至少包含 Core、Indexing、Wiki、McpServer 四个 .NET project。
- 主入口可通过 `dotnet run --project ... -- mcp --stdio` 启动 MCP stdio 服务。
- smoke test 覆盖：
  - 扫描一个小型仓库目录；
  - 写入 CodeKnowledge；
  - 生成 wiki Markdown；
  - MCP `initialize` / `tools/list` / 至少一个 `tools/call` 可用。
