# 方案设计

## 上下文
现有工作台的 graph 来源是 search/context/impact 等局部操作结果。CodeKnowledge DB 已经持久化了宏观结构事实，所以整体图不需要重新索引，也不需要依赖 vector embedding。搜索过滤则应继续留在 vector search 层，因为 `source_kind` 是 embedding relation 的字段。

## 方案概览
1. 语义搜索类别过滤
   - `VectorSearchRequest` 增加 `SourceKinds`。
   - 接受用户词汇 `code`、`docs`，内部归一到 `symbol`、`doc`。
   - 未传类别时保持全量搜索。
2. 整体图 tool
   - 在 Tools package 内新增 overview graph builder。
   - 从 `ck_repo`、`ck_file`、`ck_symbol`、`ck_doc_block`、`ck_relation` 读取 bounded facts。
   - 输出前端可直接消费的 `nodes` / `edges`。
   - `code` 类别包含 repo/file/symbol/import 类节点；`docs` 类别包含 doc block 节点。
3. Server 与前端
   - HTTP typed endpoint `/api/graph/overview` 复用 `overview_graph` tool。
   - 前端 category controls 同时用于 search 和 overview。
   - Graph tab 增加无需搜索的加载入口。

## 影响范围与修改点（Impact）
- `CozoVectorSearchService.SearchAsync` 增加 source kind predicate。
- `LlmWikiToolRunner` 增加 tool metadata 与 dispatch。
- `LlmWikiWebServer` 增加 endpoint。
- `api.ts` 增加 overview client 和 direct graph payload handling。
- `App.vue` 增加 controls/actions。

## 决策摘要
- `code` v1 映射为 symbol embeddings；整体图中也展示 file/symbol/import 结构。
- 概览图默认 bounded，避免大型仓库把前端画布打满。
- 不改变已有 DB schema；只读取既有 CodeKnowledge facts。

## 风险 / 权衡
- 大仓库概览仍可能复杂：通过 `maxNodes` / `maxEdges` 和 `truncated` metadata 缓解。
- docs-only 概览如果没有 code/file containment 会失去上下文：v1 允许保留文件容器节点来定位 doc block。
- `code` 当前不包含全文 file embedding，因为 vector index 只写 symbol/doc；后续如增加 file embeddings，可自然加入归一化映射。

## 待解决问题
- 后续可补 server-side graph layout hints 或按目录聚合的更高层 graph。
