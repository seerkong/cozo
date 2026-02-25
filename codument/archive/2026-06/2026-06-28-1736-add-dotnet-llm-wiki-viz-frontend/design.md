# 方案设计：add-dotnet-llm-wiki-viz-frontend

## 上下文

前端通过 `VITE_API_BASE` 调用 server，默认 `http://127.0.0.1:4176`。生产构建产物可由 `cozo-wiki --serve --static-dir` 服务。

## 方案概览

1. Vue 3 + Vite + TypeScript。
2. `src/lib/api.ts` 封装 server endpoints。
3. `src/components/GraphView.vue` 使用 `vis-network`。
4. `App.vue` 提供工作台布局：侧栏状态/actions，主区 tabs。
5. 所有 JSON 结果提供结构化预览，便于手工测试。
6. Graph 投影参考 GitNexus：画布只显示短语义标签，完整 id/path/snippet 放到 hover 与选中详情；边标签默认不显示。
7. Semantic search graph 使用 query/root -> source result 的投影，不把 embedding item id 暴露为可见节点。
8. 节点详情卡提供 `Open block` 弹窗入口；弹窗优先展示 search hit 的 `text`，并附带 full id 与 metadata JSON，便于从图跳到具体文件 block。

## 风险 / 权衡

- MVP graph projection 先从 search/context/impact 返回 JSON 中提取常见 id/name 字段，后续可由 server 提供专用 graph endpoint。
- 不引入复杂 UI 组件库，降低依赖和构建成本。
- 前端短标签是展示层派生，不改变 server 返回的 CodeKnowledge id；需要排查时仍可在详情面板复制完整 id。
- block modal v1 复用当前 API 已返回的 block text；如果后续需要按节点 id 懒加载完整文件片段，再补 server endpoint。
