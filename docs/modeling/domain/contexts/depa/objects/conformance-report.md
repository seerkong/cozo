---
knowledge_plane: domain
doc_role: canonical
status: active
context: depa
last_verified: 2026-07-06
---

# 符合度报告：8 维、violation 与 health_score

## Identity & Boundary

符合度报告是解释层的**唯一对外结论物**：`depa_conformance` 输出逐维逐规则的裁决树，`health_score` 输出其 display-only 摘要。报告永远按维分组，**不存在全仓单一总分**——八组分别裁决、从不合并（把八个 score 平均汇报是误用）。

## Structure

```text
report
└── dimensions[8]           # data / effect / processor / layering / fact_source / actor / overdesign / vendor
    └── rules[]             # 该维的分母规则
        ├── ruleId          # V-* 规则 id（映射见 rubrics/rule-map.md）
        ├── verdict         # GAP | PASS | BLOCKED（语义见 policies/verdict-and-coverage.md）
        ├── blockedReason   # BLOCKED 时点名缺什么（缺标注/缺观测类别/未扫描）
        └── violations[]    # GAP 时的命中列表
```

## violation 的语义要点

- `violationId` 确定性构造（规则@主体@哈希），同图重扫结果幂等。
- `subjectEntityId` 挂靠**判读实体**（capsule/impl/…），不是裸符号——命中主体的挂靠规则见扫描流程。
- `message` + `path:line` 证据可直接跳转核对；violations 按 confidence 排序。
- confidence **如实传递观测边置信度**（0.5 歧义边不冒充 1.0）；`evidence` 标 `heuristic` 的命中优先人工复核——弱化实现（implemented-weakened）的命中是"现象被捕捉"，语义罪名未证。

## health_score

- 每维 `score = PASS 规则数 / 该维规则总数`；`rulesTotal` 覆盖完整分母——不可检测规则以显式 BLOCKED 占位入分母，human-only 不入（[policies/verdict-and-coverage.md](../policies/verdict-and-coverage.md)）。
- BLOCKED 只压低 coverage，**从不计入合规**。
- display-only：分数用于展示趋势，裁决以逐规则 verdict 为准。

## 消费入口

跑法与真实样例见用户手册 [depa-report.md](../../../../../../cozo-lib-dotnet-llm-wiki/docs/depa-report.md)；工具面形态（`depa_conformance`/`health_score`/`fact_grade_map`）见 [llm-wiki context](../../llm-wiki/objects/tool-surface.md)。
