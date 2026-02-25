---
knowledge_plane: domain
doc_role: canonical
status: active
context: depa
last_verified: 2026-07-06
---

# verdict 语义与规则覆盖口径

## Rule

三种 verdict 的边界与规则分母的计法是 DEPA 结论可信的根基，任何消费方（报告解读、CI 门禁、整改决策）必须按本口径读数。**规则↔判据的逐条映射真源是 [rubrics/rule-map.md](../../../../../../cozo-lib-dotnet/src/Om.Depa/rubrics/rule-map.md)**（与 `DepaReportQueries.RulesByDimension`/`PlaceholderRuleIds` 双向机检），本文只固化口径，不复制条文。

## 三种 verdict

| verdict | 含义 |
|---------|------|
| PASS | 检测器跑了且未命中——**仅在该规则可观测范围内**合规 |
| GAP | 命中违例，携带主体与 path:line 证据 |
| BLOCKED | 没有足够输入下裁决：缺标注（未声明 capsules/layers/recoveryPaths）、缺观测类别（占位规则）、或无扫描数据；`blockedReason` 必须点名缺什么 |

**BLOCKED ≠ PASS**：BLOCKED 是"没看见"，不是"没问题"。报告宁可 BLOCKED 点名缺失输入，也不假装 PASS——这是解释层的诚实性底线。

## 32 规则分母

- 分母 = rule-map 中状态 ∈ {implemented, implemented-weakened, placeholder-BLOCKED} 的规则，共 **32 条**（24 实现 + 8 占位），按 8 维分组。
- **human-only 4 条**（D3/D4/G4/G5）是人工核查表专属，不进工具分母。
- **占位规则**恒 BLOCKED，reason 命名缺失观测类别（需语句级 AST / 需运行时语义 / 需人工语义比对）——静态符号图观测不足以裁决它们，占位是设计而非缺陷。

## implemented-weakened 的读法

弱化实现是**现象级近似**（词表匹配、外呼密度、文本交集……），rule-map 逐条注明缺口：

- GAP 命中 confidence 通常 ≤0.6–0.7、evidence 标 `heuristic`——现象被捕捉，语义罪名未证，需人工确认；
- PASS 只代表该弱化观测面干净，语义违例可能仍在。

## Enforcement Points

- 机检：rule-map-anchored 测试（`cozo-lib-dotnet/tests/Program.cs`）锁死 rule-map ↔ RulesByDimension ↔ PlaceholderRuleIds 三方一致——改任何一方必须同步另两方，流程见 [docs/impl/global/howto/depa-rule-extension.md](../../../../../impl/global/howto/depa-rule-extension.md)。
- 报告侧：`DepaReportQueries`（分母/占位/8 维分组），见 [code-map.md](../code-map.md)。
