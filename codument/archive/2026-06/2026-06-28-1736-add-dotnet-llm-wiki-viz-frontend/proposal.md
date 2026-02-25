# 变更：新增 cozo-lib-dotnet-llm-wiki-viz 前端工作台

## 背景和动机 (Context And Why)

`cozo-wiki --serve` 已提供本地 HTTP API。用户还需要一个直接面向人的前端可视化版本，用来搜索 repo DB、查看图、构建 wiki、调试 NamedQuery 和工具调用。

## "要做"和"不做" (Goals / Non-Goals)

**目标:**

- 新建 `cozo-lib-dotnet-llm-wiki-viz/` Vue 3 + Vite + TypeScript 前端。
- 使用 `vis-network` 实现图视图。
- 接入 `cozo-wiki --serve` API。
- 实现 status/index/search/graph/wiki/named-query/tools 工作流。
- 前端可 `npm run build`。

**非目标:**

- 不实现用户认证或远程部署。
- 不实现 raw CozoScript 编辑器。
- 不复制 Bun ontology/governance demo 页面。

## 变更内容（What Changes）

- 新增 package.json、Vite/TS 配置、Vue 源码。
- 新增 API client、GraphView 和工作台 App。
- README 后续 track 统一补充运行方式。

## 影响范围（Impact）

- 受影响的能力（behaviors）：`llm-wiki-viz`
- 受影响的代码：`cozo-lib-dotnet-llm-wiki-viz/**`
