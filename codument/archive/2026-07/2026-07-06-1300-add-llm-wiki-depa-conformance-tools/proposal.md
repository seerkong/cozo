# 变更：DEPA 红灯检测与健康度工具

## 背景和动机 (Context And Why)

mission `add-llm-wiki-depa-fractal-wiki`（P2）G6，DEPA 线收口。G5 已就位：depa_* 本体、三通道标注、effect 白名单、ck_external_call 观测层、DepaScanAsync 第一段（①标注 ②结构物化 ④规则 Check）。本 track 补 ③——映射设计 §3 的 8 条 V-* 红灯检测查询与 depa_violation 物化，并把结论经三个工具暴露给 agent：`depa_conformance`（四维分组报告）、`fact_grade_map`（事实源分级图）、`health_score`（展示层加权，不合并四维裁决）。

mission 成功判据在此验收：对 Om.Core（合规样本）与故意违规样本给出可区分、带 path:line 证据的结论。

## "要做"和"不做" (Goals / Non-Goals)

**目标:**
- 8 条红灯检测（映射设计 §3 表，Datalog 读 ck_*+depa_* → 物化 depa_violation + violates/effect_leaks_through/backwrites 边）：V-F1、V-L1、V-E1（静态最可靠三条先行）→ V-D1、V-S1、V-E2、V-F2、V-L3。
- violation 纪律（§5.2）：evidence_json schema（每条必有 path:line；无证据宁 BLOCKED）；confidence=min(输入标注)；id 幂等 upsert；scan_commit 绑定 + 过期清理（可重建投影）。
- DepaScanAsync 完整化（①②③④ 全段）；输入缺失的规则显式 BLOCKED+原因（§5.1，如 V-E2 依赖 static 修饰缺失时）。
- 三工具进 LlmWikiToolRunner（矩阵 14→17，单实现三入口）：depa_conformance（维度分组×verdict×violations 按 confidence 降序×BLOCKED 原因；参数 dimension 过滤）、fact_grade_map（fact_source 节点+fact_written_by/projection_derived_from 邻接）、health_score（每维 GAP 数/规则覆盖率，明示"四维分别裁决"）。
- 验收样本：tests fixture 合规样本（contract+impl 分离、经 runtime 注入）与故意违规样本（core 直调 File.WriteAllText、config 塞 Func、跨 capsule 触 internals）——结论可区分且逐条带 path:line。

**非目标:**
- V-D2/V-P1/Actor 维/传递闭包泄漏（映射设计 §3 已裁 MVP 不做，报告对应输出 BLOCKED+静态观测不足）。
- wiki 呈现 DEPA 报告页（留 G7 或后续）。

## 变更内容（What Changes）

- Om.Depa：DepaScanPipeline ③段（8 条检测 + violation 物化 + 清理）；报告聚合模型。
- Tools：三工具注册与实现。
- tests 两侧（含双样本 fixture）。

## 影响范围（Impact）

- 受影响能力：cozo-dotnet-codeknowledge（新增 depa-conformance 需求）。
- 受影响代码：cozo-lib-dotnet/src/Om.Depa/、llm-wiki packages/Tools、两侧 tests。
