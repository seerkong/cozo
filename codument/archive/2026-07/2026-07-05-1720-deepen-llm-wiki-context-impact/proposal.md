# 变更：symbol_context / impact_of_change 深图化

## 背景和动机 (Context And Why)

mission `expand-llm-wiki-agent-tools`（P1）G3 第二弹。对标 GitNexus 的 `context`（符号 360° 视图）与 `impact`（深度分层影响 + 风险评级）：cozo-wiki 的两个工具目前只做扁平边遍历，未利用 P0 的执行流/社群/confidence。同时消化 P0 交接缺口①（treesitter 调用边 caller 同名重载归属错误——E2E 抽样 #7 实证）与③（impact 的 minConfidence 未暴露为工具参数）。

## "要做"和"不做" (Goals / Non-Goals)

**目标:**
- **缺口① bug fix**：CallResolver 的 caller 归属改为按调用点行号落在符号 (start_line,end_line) 区间内定位（同名重载各自区间），不再取同名首个。
- symbol_context 深图化（add-only）：incoming/outgoing 边带 confidence（已有）+ 新增：参与执行流列表（FindProcessesForSymbolAsync）、所属社群（FindSymbolCommunityAsync）、按 kind 分组统计。
- impact_of_change 深图化：结果按深度分层（layers[d] = 该深度新增受影响符号）；direction=up（谁受我影响，沿 CALLS 反向）| down（我影响谁，沿正向）——注意语义명명以 agent 直觉为准并在 schema 描述写清；minConfidence 工具参数（缺口③）；风险评级 LOW(<4 直接受影响)/MEDIUM(4-9)/HIGH(≥10)/CRITICAL(≥10 且参与执行流 ≥3)；受影响执行流列表（有界）。
- 工具面：symbol_context/impact_of_change 参数与输出 add-only 升级；smoke 扩展。

**非目标:**
- 不做 PDG/语句级影响。
- 不改 named query 契约（code.impactOfChange v1 原样）。
- 不做分页（结果有界：每层 ≤50 符号 + 截断计数）。

## 变更内容（What Changes）

- Indexing/CallResolver：caller 归属修复（bug fix，行为回归预期）。
- Om.CodeKnowledge：ImpactOfChange 深化（分层/方向/风险/processes）+ SymbolContext 富化（add-only）。
- Tools：两工具参数与输出升级。

## 影响范围（Impact）

- 受影响能力：cozo-dotnet-codeknowledge（deep-context-impact 需求）。
- 受影响代码：cozo-lib-dotnet/src/Om.CodeKnowledge/、llm-wiki packages/{Indexing,Tools}、两侧 tests。
