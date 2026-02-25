# Decision: cozo-wiki 语义代码图地基架构（durable）

- 日期：2026-07-05（用户于 2026-07-04 确认 D1–D5）
- 来源：mission `deepen-llm-wiki-code-graph`（archived）decisions.md
- 状态：Durable · 长期项目决策

## 决策内容（后续 track/mission 均按此执行）

1. **解析器 = 混合双后端**：tree-sitter 原生 P/Invoke 统一多语言基线（grammar commit pin + ABI 启动断言，dylib 按 runtimes/<rid>/native 分发）+ Roslyn 增强 C#（轻量编译，非 MSBuildWorkspace）。两后端实现同一解析契约，落图逻辑不感知实现；同调用点最高置信来源胜（Roslyn 覆盖 treesitter）。
2. **CodeKnowledge v2 schema 是长期真源形态**：单边表 ck_edge{from,to,kind,file,line => confidence,resolver,evidence}（调用点粒度）；ck_symbol 带 sym_key/doc_id；派生层专表（community/member/process/entry_point）；ck_meta.schema_version + 重建式迁移（reindex，不搬数据）。
3. **confidence 口径**：1.0 语义命中（roslyn）/0.6 候选 / 0.9 唯一精确 / 0.7 name+arity 唯一 / 0.5 歧义（evidence 记 ambiguous）/ 0.3 regex；宁标注不静默丢，无候选只计数不落边；下游一律支持 minConfidence 过滤。符号归一键 sym_key=(lang, 全限定名, arity)，canonical ID 用 Roslyn DocumentationCommentId。
4. **图算法 CozoDB 优先**：社群检测用内置 Louvain 固定规则（已证无随机性）、循环检测用 SCC、路径用最短路固定规则——.NET 侧只做规范化/统计。这是相对 GitNexus（自维护 JS 图算法库）的差异化。
5. **派生层有界产出**：社群 max(10,min(200,symbols/20))+minSize 3；执行流 max(20,min(300,symbols/10))+minSteps 3+maxSteps 50；截断/noise 必计数。
6. **最小公开面纪律**：实现细节（extractor/resolver/merge step/query 基础设施）一律 internal，测试走 InternalsVisibleTo；公开面以 track design 的清单为准——本 mission 三次 AttractorCheck GAP 均源于违反此条。

## Why

D1–D5 由用户逐项确认（2026-07-04），并经 6 个实现 track + E2E 验证（30 抽样 ≥90% 门禁、conf 1.0 占比 85.1%）证实可行。P1/P2/P3 mission 的工具面、DEPA 分析、分形文档全部构建在这套口径之上，不应被单个后续 track 私自更改。

## 引用

- decision://llm-wiki-code-graph-foundations
- 证据：codument/archive/2026-07/ 下 7 个 track；behaviors/cozo-dotnet-codeknowledge.xml、dotnet-llm-wiki-semantic-parsing.xml
