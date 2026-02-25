# 变更：统一调用消解与语义边（tree-sitter 基线）

## 背景和动机 (Context And Why)

mission `deepen-llm-wiki-code-graph` G4 第一弹。G3 已产出符号树与结构边（CONTAINS/HAS_METHOD/EXTENDS/IMPLEMENTS/IMPORTS），但图中还没有 CALLS/ACCESSES/METHOD_OVERRIDES——这是 impact/trace/DEPA 分析的核心边。本 track 在 tree-sitter 基线上做调用点提取与启发式消解（C#/TS/JS），产出带 confidence 的调用边；C# 的语义级增强（Roslyn，confidence 1.0）由下一 track `add-llm-wiki-roslyn-csharp-resolution` 叠加。

## "要做"和"不做" (Goals / Non-Goals)

**目标:**
- 调用点提取：C# invocation/object_creation、TS call/new expression（extractor 增强，含接收者文本与实参 arity）。
- 仓内符号注册表（per-index 构建）：sym_key/名称/arity 索引，支持同文件 → import 约束 → 仓内全局的分层匹配。
- CALLS 边消解（启发式置信度分级）：唯一精确匹配（同文件或 import 约束内）0.9；仓内 name+arity 唯一 0.7；多候选取名称匹配记 0.5（evidence 记 ambiguous:n）；无匹配不落边（记 diagnostic 计数）。`new X()` → X 的 constructor（无显式构造则指向类型符号本身）。this/self 成员调用 → 所属类型及其 EXTENDS 链成员。
- METHOD_OVERRIDES / METHOD_IMPLEMENTS：基于 EXTENDS/IMPLEMENTS 边 + 成员 name+arity 匹配。
- ACCESSES 边（预算开关）：成员访问（读/写在 evidence 标注）仅在接收者可消解时落边，per-file 上限（默认 200，可配），可整体关闭。
- dogfood：对本仓索引后 CALLS 边规模与抽样正确性报告（为 G6 的 ≥90% 门禁做首次测量）。

**非目标:**
- 不做 Roslyn 语义消解（下一 track）。
- 不做跨仓/外部依赖符号（BCL 调用不落边，只计数）。
- 不做数据流/类型推导 fixpoint（GitNexus 式 TypeEnv 留后续演进）。

## 变更内容（What Changes）

- SemanticParsing：extractor 增加调用点/成员访问捕获（ParsedCallSite 契约扩展）。
- Indexing：新调用消解阶段（RepositoryIndexer 索引后处理或独立 CallResolver），写 CALLS/ACCESSES/METHOD_OVERRIDES 边（resolver=treesitter）。
- Core：索引请求增加 ACCESSES 开关与预算参数（add-only）。

## 影响范围（Impact）

- 受影响能力：dotnet-llm-wiki-semantic-parsing（扩展 call-resolution 需求）。
- 受影响代码：packages/{SemanticParsing,Indexing,Core}、tests。
