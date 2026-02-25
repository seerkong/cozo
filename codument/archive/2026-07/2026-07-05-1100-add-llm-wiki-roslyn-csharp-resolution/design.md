# 方案设计：Roslyn C# 语义增强消解

## 上下文

spike（mission analysis/spike-roslyn-callgraph.md）结论：ParseText+CSharpCompilation + TPA 全量引用 + ImplicitUsings 模拟 → 1.0 命中 98.8%；符号 canonical ID 用 GetDocumentationCommentId()；性能 30 文件 ~0.5s。G4-T1 产物：CallResolver（treesitter 基线边）、ck_symbol（sym_key/doc_id 列就位）。

## 方案概览

1. **新包 Cozo.DotNet.LlmWiki.RoslynEnhancer**（capsule，最小公开面）
   - public 入口仅一个：`RoslynEnhancer.Analyze(IReadOnlyList<(string path, string source)> csFiles) → RoslynAnalysisResult`（或等价 record）；其余全部 internal。
   - 内部：一次性构建 compilation（全部 .cs 语法树 + TPA `AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")` 引用 + global usings 注入模拟 ImplicitUsings）；遍历 InvocationExpressionSyntax/ObjectCreationExpressionSyntax；SymbolInfo.Symbol → 1.0，CandidateSymbols 非空 → 0.6+CandidateReason；caller = 包含调用点的方法/构造/属性访问器符号。
   - 输出：`RoslynCallEdge`（callerDocId、calleeDocId、callee 是否 source 内（Locations.IsInSource）、file、line、confidence、evidence）与 `RoslynSymbolInfo`（DocId、sym_key 等价字段：命名空间限定名+arity、file、startLine、endLine）。
   - 仓外 callee（非 source）只计数（ExternalCalls），不出边。
2. **Indexing 归并**（internal RoslynMergeStep）
   - 符号匹配：Roslyn sym_key（lang=csharp+限定名+arity）→ ck_symbol.sym_key 相同且行区间重叠 → 填 doc_id（CodeSymbolFact 更新，resolver 保持 treesitter 但 doc_id 来自 roslyn；记录 findings 口径）。
   - 边归并 key = (callerSymbolId, file, line)：Roslyn 边命中 → 移除该 key 的 treesitter CALLS 边，插入 roslyn 边（callee 经符号匹配映射到 symbol id；映射失败则保留基线边并计数 unmapped）。
   - Roslyn 独有边（基线该调用点无边，如基线 unresolved）→ 补插。
   - 开关：RepositoryIndexRequest.EnableRoslynEnhancement（默认 true，仅 C#）；Summary add-only：RoslynResolvedCalls/RoslynCandidateCalls/RoslynExternalCalls/RoslynUnmappedCalls。
3. **性能与失败安全**：compilation 构建失败/抛错 → 记 diagnostic，整体索引照常（基线已可用）；文件数上限可配（默认不限，记录耗时进 summary 或 findings）。

## 影响范围与修改点（Impact）

- 新包 + Indexing 引用；RepositoryIndexer 在 CallResolver 之后跑 RoslynMergeStep（仅 C# 文件集）。
- Core add-only 字段；tests。

## 决策摘要

- 归并 key 用调用点 (caller,file,line) 而非目标——同一点只留最高置信来源（Roslyn 胜），与 mission A1/design §6 一致。
- doc_id 富化不改 resolver 字段语义（resolver 标产出来源，边为 roslyn、符号保持 treesitter+doc_id），记录进 findings。

## 风险 / 权衡

- 不完整编译退化（spike：仅 object 时 74%）→ TPA 全量 + implicit usings（spike 已证 98.8%）；仍失败按 0.6/unmapped 兜底。
- Roslyn 包体积/冷启动 → 只在 C# 文件存在且开关开时构建 compilation。
- 多 target/条件编译差异 → 非目标，如实计数。

## 最小公开面

RoslynEnhancer 一个 public 入口 + 结果 record；RoslynMergeStep internal；测试走 InternalsVisibleTo。
