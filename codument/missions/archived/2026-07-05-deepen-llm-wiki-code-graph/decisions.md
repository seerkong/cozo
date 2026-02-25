# Decisions · deepen-llm-wiki-code-graph

## 2026-07-04 · D1–D5 经用户确认（详见 analysis/decision-tree.md）

1. **D1 解析器 = 混合**：tree-sitter 原生 P/Invoke 统一多语言基线 + Roslyn 增强 C#。两套提取器输出对齐同一套 v2 图 schema；解析后端走契约 + 实现分离（capsule），CLI adapter 降级为备选后端。候选 durable：解析后端契约模式。
2. **D2 首批语言 = C# + TS/JS**。
3. **D3 schema = v2 重设计 + 迁移**：参照 GitNexus 节点/边类型全集裁剪，边带 confidence；索引可重算，迁移以"重建索引 + 工具适配"为主，12 工具全量回归为硬门禁。候选 durable：v2 schema 即 CodeKnowledge 的长期真源形态。
4. **D4 社群检测 = CozoDB 内置 Louvain**（已查证 cozo-core fixed_rule 存在），聚类算法作可替换策略契约，为 Leiden 留接缝。
5. **D5 执行流入口 = public API 面 + Main/CLI + ASP.NET 路由/MCP 工具定义**；测试入口后置。
