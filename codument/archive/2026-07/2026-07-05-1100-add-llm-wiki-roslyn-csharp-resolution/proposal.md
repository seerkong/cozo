# 变更：Roslyn C# 语义增强消解

## 背景和动机 (Context And Why)

mission `deepen-llm-wiki-code-graph` G4 第二弹（D1 混合决策的 Roslyn 半边）。G4-T1 的 tree-sitter 基线消解率 55.7%（本仓 dogfood），启发式 confidence 上限 0.9。G1 spike 证明 Roslyn 轻量编译（ParseText + CSharpCompilation，非 MSBuildWorkspace）对本仓 2678 调用点 confidence=1.0 命中 98.8%，性能千文件 ~10s。本 track 把 Roslyn 语义遍历叠加到 C# 索引：DocId 级 canonical 符号 ID、语义级 CALLS 边（1.0/0.6），按 sym_key+位置与 tree-sitter 基线归并（Roslyn 胜）。

## "要做"和"不做" (Goals / Non-Goals)

**目标:**
- 新 capsule 包 `Cozo.DotNet.LlmWiki.RoslynEnhancer`：全仓 .cs 轻量编译（TPA 全量引用 + ImplicitUsings 模拟），语义遍历 invocation/object_creation → 调用边（caller/callee DocId + 位置 + confidence 1.0 语义命中 / 0.6 候选+CandidateReason）与符号富化（DocId/精确签名）。
- Indexing 归并：sym_key + 行区间重叠匹配既有 ck_symbol → 补 doc_id（resolver 升级 roslyn）；同调用点（file+line+caller）CALLS 边 Roslyn 覆盖 tree-sitter（resolver=roslyn）；Roslyn 独有的仓内目标边补插；`EnableRoslynEnhancement` 开关（默认 true，仅作用于 C# 文件）。
- dogfood：本仓 C# 消解率与 confidence 分布前后对比记录。

**非目标:**
- 不落仓外目标（BCL）边（与 G4-T1 口径一致，只计数）。
- 不用 MSBuildWorkspace / 不解析 sln、csproj 条件编译。
- 不做 TS/JS（Roslyn 仅 C#）。
- 不改既有查询/工具面。

## 变更内容（What Changes）

- 新包 packages/Cozo.DotNet.LlmWiki.RoslynEnhancer（依赖 Microsoft.CodeAnalysis.CSharp，Indexing 引用之）。
- Indexing：归并阶段接线 + 开关；Summary add-only：RoslynResolvedCalls/RoslynCandidateCalls 计数。
- tests：归并用例 + dogfood 对比。

## 影响范围（Impact）

- 受影响能力：dotnet-llm-wiki-semantic-parsing（新增 roslyn-enhancement 需求）。
- 受影响代码：新包、Indexing、Core（add-only 字段）、tests。
