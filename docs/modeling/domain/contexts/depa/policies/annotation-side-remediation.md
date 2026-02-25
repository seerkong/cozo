---
knowledge_plane: domain
doc_role: canonical
status: active
context: depa
last_verified: 2026-07-06
---

# 标注纪律：depa-map、事实源分级与误报处置

## Rule

解释层的输入判断（capsule 边界、事实源分级、层次声明、豁免）统一落在 **`depa-map.json` 标注**与**白名单目录**里；确认为误报/豁免的命中，处置路径是**改标注，不改业务代码**——不为让报告变绿而扭曲实现。

## depa-map 标注面

- **capsule / contract / impl / fact_source 声明**：判读实体的来源（assigned_by=config）；未声明相应标注的规则恒 BLOCKED（宁缺勿猜）。
- **fact_source 分级**：grade 1–7（authoritative_fact → surface_view），判据真源 [rubrics/fact-source-truth.md](../../../../../../cozo-lib-dotnet/src/Om.Depa/rubrics/fact-source-truth.md) 与 [fact-grade-classification.md](../../../../../../cozo-lib-dotnet/src/Om.Depa/rubrics/fact-grade-classification.md)；grade 决定单写者期望（1–3）、快照/流水账读取判定（4–5）、投影语义（6–7）。
- **add-only 键**：`layers[]`（`{name, pathGlobs[]}`，低→高声明，V-P2 分层判定依据）与 `recoveryPaths[]`（恢复路径 glob，V-S2a/b 豁免合法恢复读取）——扩展键只增不改，旧标注永远有效。
- **副作用白名单**：`depa_effect_api` 目录（FQN glob + category + direction）划定"什么算外部副作用"；exempt_contract 类别承接 DEPA 自身范式的豁免。

## 误报处置路径

1. 核对 violation 的 path:line 证据与 confidence/evidence（heuristic 优先怀疑）。
2. 确认误报/豁免 → 编辑 depa-map（补 capsule/分级/layers/recoveryPaths 或白名单），重扫验证该命中消失。
3. `fact_grade_map` 导出当前分级图核对标注生效。
4. 真违例 → 走整改闭环（[workflows/scan-pipeline.md](../workflows/scan-pipeline.md)）。

## Rationale

判断与代码分离：架构判读会被推翻/细化，标注侧迭代不污染业务提交历史；"未声明恒 BLOCKED"迫使判断显式化，杜绝检测器替用户猜边界。

## Enforcement Points

`DepaConfigModels`（depa-map 解析）、`DepaEffectCatalog`（白名单）、检测器的 BLOCKED 分支（见 [code-map.md](../code-map.md)）；操作指南见用户手册 [depa-report.md](../../../../../../cozo-lib-dotnet-llm-wiki/docs/depa-report.md) 误报处置节。
