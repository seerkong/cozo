# Design：Tree-sitter Semantic Parsing

## 上下文

Tree-sitter 是语法 AST 层能力，不等同于 Roslyn 的 C# 编译语义。当前需求明确“暂时只支持 Tree-sitter”，因此第一版不引入 Roslyn fallback。

## 方案概览

1. 新增 `SemanticParsing` package。
2. 定义模型：
   - `SemanticParseRequest`
   - `SemanticParseResult`
   - `SemanticNodeSummary`
   - `SemanticDiagnostic`
3. 实现 `TreeSitterCliParser`：
   - 检查 `tree-sitter --version`。
   - 调用 `tree-sitter parse <file>`。
   - 解析 CLI 文本输出为粗粒度 node summary。
   - CLI 缺失或失败时返回 diagnostic。
4. MCP tools：
   - `parser_status`
   - `parse_file`

## 影响范围与修改点（Impact）

- 不修改 `Om.Core`。
- 不要求 Indexing 立刻切到 AST 抽取；先提供独立 parser facade，后续可在 Indexing 中消费。

## 风险 / 权衡

- CLI adapter 依赖用户本机安装 Tree-sitter。
- 文本输出解析比 native AST 绑定弱，但进程边界稳定，适合作为 MVP。
