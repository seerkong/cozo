# Mission：add-dotnet-llm-wiki-viz

## 背景和动机

`cozo-lib-dotnet-llm-wiki` 已经具备 GitNexus-like 的核心后端能力：仓库扫描、CodeKnowledge 写入、wiki 编译、MCP stdio、CLI tools、Tree-sitter 解析、Cozo 向量检索、本地 ONNX MiniLM embedding、`.gitignore` 忽略规则和关系生成预算控制。

现在缺少的是一个给人直接使用的本地交互工作台：能够查看 repo DB 状态、触发索引、做语义搜索、查看 symbol context/impact graph、构建并预览 wiki、调试 NamedQuery。`cozo-lib-bun-viz` 已提供 Vue/Vite/vis-network 的可视化工具参考，但它面向本体治理 demo，不能直接复用 API 与页面语义。

## 目标

- 新建 `cozo-lib-dotnet-llm-wiki-viz/` 前端项目，提供 repo knowledge 搜索、图可视化、wiki 与 query 调试工作台。
- 为 `cozo-wiki` CLI 添加 `--serve` server mode，启动本地 HTTP API，并支持服务前端静态资源。
- 新增或抽出共享 tool runner/contracts，使 MCP stdio、CLI `call`、HTTP API 使用同一套后端能力。
- 新增 `Cozo.DotNet.LlmWiki.Server` package，提供 ASP.NET Core minimal API。
- 支持开发模式下 Vite 前端调用本地 API，生产模式下 `cozo-wiki --serve` 可服务已构建的 frontend dist。
- 为 server/API/前端核心流程提供 smoke 或 E2E 验证，并更新 README/help。

## 非目标

- 不在 MVP 中实现多用户、认证、远程部署或权限模型。
- 不直接复制 `cozo-lib-bun-viz` 的 ontology/governance demo API。
- 不默认开放 raw CozoScript 任意执行端点。
- 不重写现有 indexing、wiki、vector search、MCP 协议实现。
- 不把 HTTP/presentation 逻辑沉入 `Om.Core` 或 CodeKnowledge ontology 层。

## 成功判据

- `cozo-wiki --serve --work-dir <repo>` 能启动本地 server，默认使用现有 sqlite repo DB layout。
- `GET /health` 和主要 `/api/*` endpoints 可用，且调用同一套 tool runner。
- 前端可完成至少这些流程：查看状态、触发索引、语义搜索、查看搜索结果、从 symbol/impact 数据渲染图、触发 wiki build、运行 named query 或 tool call。
- 前端构建通过，server/API smoke 通过；如条件允许，Playwright E2E 覆盖启动 server + 访问工作台。
- README 与 `--help` 说明 `--serve`、端口、static-dir/dev 模式和常用工作流。

## 为什么需要 mission 而不是单个 track

这个目标横跨多个工程面：共享工具层重构、ASP.NET server、CLI 参数与帮助、Vue/Vite 前端、API/前端契约、E2E 验证。它们可以并行规划但需要按依赖落地，并且执行期可能根据 API contract 或前端交互发现新的 gap。因此 mission 负责期望态 DAG 和受控重规划，真实代码改动仍由后续 tracks 落地。
