# 变更：tree-sitter 原生解析 ingestion（替换正则主路径）

## 背景和动机 (Context And Why)

mission `deepen-llm-wiki-code-graph` G3。cozo-wiki 的符号提取目前是 RepositoryIndexer 里的正则启发式，SemanticParsing 包只有外部 tree-sitter CLI adapter（解析结果不进图）。G1 spike 已证明 .NET 10 P/Invoke 内嵌 tree-sitter 完全可行（3 个 arm64 dylib 秒级编译、解析与 query API 全通、ABI [13,15] 兼容），spike 产物在 /tmp/spike-treesitter-pinvoke（本 track 应把可复用部分正式化进仓库）。

本 track 落地 D1 决策的 tree-sitter 统一基线：解析后端契约 + P/Invoke 后端 + C#/TS per-language extractor，符号与结构边写入 v2 图（G2 已就位），正则降级为无 grammar 语言的 fallback。

## "要做"和"不做" (Goals / Non-Goals)

**目标:**
- 解析后端契约（capsule：契约与实现分离），现有 CLI adapter 降级为一种后端。
- tree-sitter P/Invoke 原生绑定（SafeHandle、ABI 版本启动断言、grammar commit pin）+ 构建脚本 + macOS arm64 dylib 入库（runtimes/<rid>/native 模式，对齐 cozo-lib-dotnet 惯例）。
- C# 与 TS/JS extractor（tree-sitter query 驱动）：符号（class/interface/struct/enum/record/method/constructor/property/field/function/…含嵌套 parent_id、visibility、exported、signature、sym_key）与结构边（CONTAINS/HAS_METHOD/HAS_PROPERTY/EXTENDS/IMPLEMENTS/IMPORTS），resolver=treesitter。
- RepositoryIndexer 集成：有 grammar 的语言走 tree-sitter 主路径，无 grammar 语言与 tree-sitter 初始化失败时降级 regex（保留既有行为），docs/doc_links 逻辑不变。
- 对本仓 dogfood：索引 cozo-lib-dotnet*（C#）与 viz 前端（TS）符号数 ≥ 正则版。

**非目标:**
- 不做 CALLS/ACCESSES/METHOD_OVERRIDES 调用消解（G4 track）。
- 不做 win/linux dylib 产出（构建脚本支持，产物本 track 只交付 osx-arm64；平台矩阵扩展留 P3 mission 或发布期）。
- 不做 .tsx 方言与更多语言。
- 不动 embedding/wiki/tools 面。

## 变更内容（What Changes）

- SemanticParsing 包：新增 native binding + backend 契约 + C#/TS extractor + runtimes dylib + build-native-parsers.sh。
- Indexing 包：RepositoryIndexer 主路径切 tree-sitter，regex 降级 fallback。
- tests：extractor 单测（固定样例断言符号/边）、集成断言（dogfood 计数与抽样）。

## 影响范围（Impact）

- 受影响能力：dotnet-llm-wiki-semantic-parsing（扩展）、cozo-dotnet-codeknowledge（消费其 v2 事实模型，不改）。
- 受影响代码：cozo-lib-dotnet-llm-wiki/packages/{SemanticParsing,Indexing}、tests。
