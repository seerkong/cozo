# 变更：增加 Tree-sitter Java 索引基线

## 背景和动机 (Context And Why)

C# 版 depa-wiki 已有 Tree-sitter 原生解析和 per-language extractor，但 Java 文件尚未进入默认索引，目标 Spring 仓库因此只能得到文件/文档事实。

## "要做"和"不做" (Goals / Non-Goals)

目标：

- 接入固定版本的 tree-sitter-java native grammar。
- 默认纳入 `.java` 并识别 language=`java`。
- 实现 Java 符号、结构边和调用站点 extractor。
- 保持既有语言与 fallback 行为兼容。

非目标：

- 不在本 track 实现跨文件 Java 调用消解。
- 不推导 Spring 框架角色和路由。
- 不要求 Maven 编译或依赖解析。

## 变更内容（What Changes）

- 扩展 native grammar 构建、加载、ABI 检查和打包。
- 新增 query-driven `JavaExtractor`。
- 扩展索引文件路由和 CLI language support。
- 增加 Java fixture、native backend、fallback 和 indexer regression 测试。

## 影响范围（Impact）

- behaviors：`dotnet-llm-wiki-semantic-parsing`
- code：SemanticParsing、Indexing、native parser build assets、tests
