---
knowledge_plane: global
doc_role: howto
status: active
last_verified: 2026-07-06
---

# 新增 DEPA 检测规则

## When To Use

要把 rubrics 判据目录中一条 placeholder-BLOCKED 规则升级为实现，或新增一条 V-* 规则。

## Preconditions

先读判读语义真源：[verdict 与分母口径](../../../modeling/domain/contexts/depa/policies/verdict-and-coverage.md)、[rule-map.md](../../../../cozo-lib-dotnet/src/Om.Depa/rubrics/rule-map.md)（判据 ↔ 规则三方映射）。

## Steps（三方必须同步，机检锁定）

1. **fixture 先行（测试驱动）**：在 `cozo-lib-dotnet/tests/Program.cs` 为新规则写正 case（应命中 GAP）与反 case（应 PASS）；输入不足的形态写 BLOCKED case（未声明标注恒 BLOCKED）。
2. **实现检测器**：在 `cozo-lib-dotnet/src/Om.Depa/DepaViolationDetectors.cs` 加 `Detect<Rule>`——先判输入充分性（缺标注/观测 → BLOCKED + reason 点名缺什么），再判命中；violation 如实携带观测边 confidence，现象级近似的命中 evidence 标 `heuristic`。
3. **登记 rule-map**：更新 `rubrics/rule-map.md` 对应行的状态（implemented / implemented-weakened，弱化必须注明缺口）；若涉及维度归属调整同步备注。
4. **对齐报告分母**：`DepaReportQueries.RulesByDimension` / `PlaceholderRuleIds` 与 rule-map 双向一致——rule-map-anchored 测试会机检，改一方不改另两方直接红。
5. **需要新标注键时**：depa-map 扩展键遵守 **add-only**（如 layers[]/recoveryPaths[] 先例），旧标注不失效；同步 [annotation 纪律](../../../modeling/domain/contexts/depa/policies/annotation-side-remediation.md)。

## Verification

```bash
cd cozo-lib-dotnet/tests && dotnet run     # fixture + rule-map-anchored 机检全绿
```

再对本仓复扫（`depa_conformance`）做真伪裁定：新规则的真实命中逐条核对 path:line，误报回到检测器精度或标注侧。

## Rollback/Recovery

规则实现有误时回退检测器与 rule-map 行状态即可；已物化的 violation 随下次重扫自动消失（幂等重建）。

## Related Rules

规则条文**不复制**进本文与 modeling——真源永远是 `src/Om.Depa/rubrics/`。
