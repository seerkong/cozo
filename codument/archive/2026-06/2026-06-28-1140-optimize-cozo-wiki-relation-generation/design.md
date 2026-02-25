# 方案设计

## 上下文

`documents` 边当前由 Markdown 文本包含符号名推断得出。这类边不是 AST/导入/定义等结构事实，置信度低且候选空间巨大。GitNexus 的经验是：结构事实可以写图，弱文本关联应通过搜索 top-k 或带预算的有限推断处理。

## 方案概览

1. 增加 doc-symbol link 模式
   - `Local`：默认，只在路径邻近或路径 token 相关的文档与符号之间匹配。
   - `Off`：完全不生成推断 `documents` 边。
   - `Global`：显式启用旧类全局匹配，但仍受预算限制。

2. 增加预算
   - `MaxDocLinksPerDoc` 默认 20。
   - `MaxInferredRelations` 默认 200000，约束推断关系总量；结构关系仍保留。

3. 提升匹配质量
   - 跳过过短、常见、歧义高的符号名。
   - 使用大小写无关但按单词边界匹配，避免 `Id`、`Run` 等短名爆炸。
   - 对候选按路径邻近和名称长度排序，每个 doc 只保留 top-k。

4. CLI/MCP 可配置
   - `cozo-wiki call index_repo --doc-symbol-link-mode off|local|global --max-doc-links-per-doc 20 --max-inferred-relations 200000`

## 影响范围与修改点

- Core request contract：新增枚举与 request 字段。
- Indexing：替换 `LinkDocsToSymbols` 算法。
- MCP/CLI：扩展 tool schema 和参数解析。
- Tests：增加关系预算回归用例。
- README：补充索引预算说明。

## 决策摘要

- 默认选择 `Local`，而不是 `Off`，因为现有 `docs_for_code` 需要保留高置信文档链接。
- 全局弱文本召回应优先由 `semantic_search` 承接；`Global` 仅用于显式调试或小仓库。

## 风险 / 权衡

- 默认 `documents` 边减少，部分跨目录 Markdown 提及不会被 `docs_for_code` 直接返回。缓解：使用 `semantic_search` 做全局文本召回，后续可增加查询时 doc-symbol search named query。
- `Local` 路径启发式不是完整语义解析。缓解：它只用于控制弱关联预物化，不影响结构事实。

## 待解决问题

- 后续是否增加 BM25-like 文档搜索 relation/query facade。
