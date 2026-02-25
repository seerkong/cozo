---
knowledge_plane: domain
doc_role: canonical
status: active
last_verified: 2026-07-01
---

# Domain Glossary

| Term | Meaning |
|------|---------|
| OM | Object Model 层，位于 raw Cozo stored relations 之上、ontology/rule 等高层能力之下。 |
| Type | 用来分类 entity 并定义可见 attribute 的命名 schema 概念。 |
| Mixin | 可复用 attribute 包。它贡献属性，但不是 subtype parent。 |
| Effective Attribute | mixin、ancestor、self definitions 合成后，一个 type 实际可见的 attribute definition。 |
| Alias | 从旧名或替代名到 canonical name 的兼容映射，可用于 type、relation、attribute。 |
| Validity | 可作为 attribute value 使用的 Cozo temporal value type，不等同于 property valid time。 |
| CodeKnowledge (ck_*) | 代码知识图谱观测层：从仓库可重算的文件/符号/边/文档事实与派生层 stored relations。 |
| Edge Confidence | ck_edge 携带的解析强度（0.5/0.7/0.9 分档，Roslyn 语义边 1.0/0.6），下游按其过滤或如实携带。 |
| Community | 对 CALLS+IMPORTS 加权投影跑 Louvain 发现的边界单元（ck_community），与人工声明的 capsule 不同源。 |
| Process | 从入口点沿高置信 CALLS 边 BFS 得到的执行流步骤链（ck_process），非精确控制流。 |
| External Call | 出仓（BCL/第三方）调用的低保真聚合摘要（ck_external_call），DEPA effect 维判读的观测输入。 |
| Hybrid Search (RRF) | 向量与 BM25 通道按名次以 Reciprocal Rank Fusion 融合的检索排序；向量缺失时降级纯 BM25。 |
| DEPA | 架构符合度解释层：在 ck_* 观测之上以 depa_* 本体附加判断并输出 8 维符合度报告。 |
| Capsule | DEPA 判读中人工声明的自足模块单元（depa_capsule），期望单一稳定入口。 |
| Fact Source Grade | 事实源阶梯 1–7（authoritative_fact → surface_view），决定单写者期望与投影/快照读取判定。 |
| GAP / PASS / BLOCKED | DEPA 规则三种 verdict：命中违例 / 可观测范围内合规 / 输入不足无法裁决；BLOCKED ≠ PASS。 |
