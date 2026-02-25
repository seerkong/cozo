---
knowledge_plane: domain
doc_role: canonical
status: active
context: depa
last_verified: 2026-07-06
---

# 扫描管线与整改闭环

## Trigger

`DepaScanAsync`（或工具面的 `depa_conformance` 带 scanFirst）；前置是仓库已有 `ck_*` 索引（[code-knowledge workflows](../../code-knowledge/workflows/indexing.md)）。

## 扫描步骤

1. **标注装载**：解析 `depa-map.json` 与副作用白名单，把 capsule/contract/impl/fact_source/layers/recoveryPaths 等判断物化为 `depa_*` 实体与关系（assigned_by=config）。锚定用 `sym_key`，重索引后可再锚定。
2. **观测采集**：从 `ck_edge`/`ck_external_call`/`ck_entry_point` 读取判读所需事实；出仓副作用调用按白名单目录分类。
3. **检测**：`DepaViolationDetectors` 逐规则运行——每条规则先判输入充分性（缺标注/缺观测 → BLOCKED 并写 reason），再对充分输入判命中。命中主体挂靠 impl/capsule 判读实体，capsule 外符号的命中被跳过；violation 如实携带观测边 confidence 与 evidence。
4. **物化**：violation 以确定性 id 写入本体（`violates` 关系挂主体），幂等——同图重扫结果一致。
5. **报告**：`DepaReportQueries` 按 8 维 × 32 规则分母聚合 verdict（[policies/verdict-and-coverage.md](../policies/verdict-and-coverage.md)），输出符合度报告与 health_score。

## 整改闭环（rectification loop）

对每条 GAP 二选一，然后重扫验证：

- **真违例** → 改代码（本仓先例：DEPA 符合度盘点后按 REC 建议整改 config-at-entry/timeprovider 等，再复扫确认零命中）。
- **误报/豁免** → 走标注侧（[policies/annotation-side-remediation.md](../policies/annotation-side-remediation.md)），不改业务代码。

BLOCKED 的消化路径是第三种：补齐缺失标注让规则**可裁决**——BLOCKED 数下降本身就是覆盖率改善。

## Failure Semantics

- 无索引或未扫描时报告显式 `scanned:false`/BLOCKED，不产出伪结论。
- 检测器对未标注区域一律跳过或 BLOCKED，绝不外推。

## 新增规则

扩展检测规则须保持 rubrics ↔ 检测器 ↔ rule-map 三方一致（机检锁定），流程见 [docs/impl/global/howto/depa-rule-extension.md](../../../../../impl/global/howto/depa-rule-extension.md)。
