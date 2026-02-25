# 方案设计：context/impact 深图化

## 上下文

P0 产物：ImpactOfChangeAsync(minConfidence 重载，直连递归 CozoScript)、FindSymbolContextAsync、FindProcessesForSymbolAsync、FindSymbolCommunityAsync、CALLS 边 confidence。E2E #7 实证 caller 重载归属缺陷（CallResolver 用 sym_key/name 首个匹配，未按行区间）。

## 方案概览

1. **缺口① 修复**（Indexing/CallResolver）：caller 归属查找从"同名/同 qualified 首个"改为"callerQualified 匹配 + 调用点 line ∈ [start_line, end_line] 区间"（多个匹配取区间最小者）；找不到区间匹配退回原逻辑（记 evidence caller_fallback）。回归：既有 callrepo fixture + 新重载专用 case。
2. **DeepImpactAsync**（Om.CodeKnowledge，新 public；旧 ImpactOfChangeAsync 保留兼容）
   - options：Direction Up|Down（Up=谁依赖我/受我影响，沿 CALLS 反向遍历；Down=我依赖谁）、MaxDepth=3（上限 16）、MinConfidence=0.0、MaxPerLayer=50。
   - 实现：逐层 BFS（.NET 侧，边一次拉入内存或逐层 Datalog——取简单正确者）；layers[d]=首达符号（带 name/file/line/最高 confidence 入边）；截断计数。
   - 风险评级：direct=layers[1].total（含截断前真实数）；LOW<4 / MEDIUM 4-9 / HIGH≥10 / CRITICAL=HIGH 且 root 参与执行流 ≥3（FindProcessesForSymbolAsync）。
   - AffectedProcesses：root 与 layers 成员参与的执行流去重（有界 20）。
3. **SymbolContext 富化**：FindSymbolContextAsync 结果 add-only 字段：Processes（≤10）、CommunityId/CommunityLabel、IncomingByKind/OutgoingByKind 统计（Dictionary<string,int>）。
4. **工具面**：impact_of_change 参数 add-only（direction/maxDepth/minConfidence），输出含 layers/risk/affectedProcesses（保留旧字段兼容）；symbol_context 输出 add-only。schema 描述写清 direction 语义（up=callers of me transitively / down=my callees）。
5. **smoke**：delta 6 case + 14 工具矩阵不回归。

## 影响范围与修改点（Impact）

- Indexing/CallResolver.cs（bug fix）；Om.CodeKnowledge（DeepImpact + context 富化 + models add-only）；Tools（两工具升级）；两侧 tests。

## 决策摘要

- 旧 ImpactOfChangeAsync/named query 不动（兼容）；新能力走 DeepImpactAsync。
- direction 命名以 agent 直觉：up=影响我的上游传播（callers），down=我的依赖面（callees）。

## 风险 / 权衡

- 层界限内存遍历大仓性能 → MaxPerLayer + MaxDepth 上限；拉边按 minConfidence 预过滤。
- caller 修复可能改变既有边基数 → 回归断言更新按真实语义（修复是回归预期行为）。

## 最小公开面

public：DeepImpactAsync + DeepImpactOptions/DeepImpactResult/ImpactLayer/RiskLevel + SymbolContext add-only 字段。其余 internal/private。
