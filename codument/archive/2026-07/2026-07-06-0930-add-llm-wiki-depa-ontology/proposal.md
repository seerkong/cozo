# 变更：DEPA OM 本体与 effect API 标注

## 背景和动机 (Context And Why)

mission `add-llm-wiki-depa-fractal-wiki`（P2）G5。DEPA 健康度分析（G6 工具）需要先把理论概念物化为可查询的图事实。G1-T2 已定稿完整映射设计（mission analysis/depa-om-mapping.md，本 track 的 design.md 内嵌其全文以自包含）：depa_* OM 本体层（11 个 Type + 结构/违反关系 + 5 条 ExistentialRule）+ 三通道角色标注（depa-map.json/attribute/heuristic）+ effect 白名单（depa-effects.json）+ ck_external_call 观测层扩展（唯一动索引器的项——P0 口径"仓外调用只计数"对 V-E1 不够，增设低保真摘要关系）。

本 track 落地映射设计的 §6 顺序 1-2：schema/标注/白名单/外呼摘要；8 条红灯检测与工具（§6 顺序 3-5）归 G6 track。

## "要做"和"不做" (Goals / Non-Goals)

**目标:**
- depa_* 本体 schema：11 Type（depa_node 根 + capsule/contract/impl/reducer/projection/fact_source/runtime_param/runtime_carrier/entry/effect_api/violation）+ §2.1 结构关系 + §2.3 五条 ExistentialRule（Check 模式），经 OM API（DefineType/DefineRelation/DefineExistentialRule）一键初始化（InitDepaOntologyAsync）。
- effect 白名单：内置起步表（§4.1）编译进库 + depa-effects.json 用户扩展（追加/覆盖、glob 匹配）；每条 pattern 物化 depa_effect_api 实体。
- 角色标注三通道：depa-map.json 解析（capsule 根/internals glob/contract 包/carrier 类型/fact_source 定级表，assigned_by=config）+ 启发式兜底（目录名/接口注入/命名，assigned_by=heuristic confidence≤0.7）；C# Attribute 通道本 track 只留词表与解析接缝（非目标注①）。
- ck_external_call 观测层：索引器在丢弃仓外 CALLS 目标前落摘要行（caller_id,target_key => count,category,first_file_id,first_line,resolver）；索引期用内置白名单预分类。
- depa_scan 第一段：标注同步（config+heuristic → depa_* 实体，幂等 upsert）+ 结构关系物化（§2.1 的 capsule_contains/exposes/depends_on/contract_implemented_by/entry_delegates_to；fact_written_by 由 ACCESSES-write 上溯）+ ExistentialRule Check 输出 BLOCKED/GAP 行（检测查询红灯归 G6）。

**非目标:**
- 8 条 V-* 红灯检测与 depa_conformance/fact_grade_map/health_score 工具（G6）。
- ① C# Attribute 标注的索引器支持（留接缝）。
- depa_mailbox/depa_actor/depa_dispatcher（映射设计已裁）。

## 变更内容（What Changes）

- Om.CodeKnowledge（或新 Om.Depa capsule——design 定）：本体 schema init、白名单/映射配置解析、depa_scan 标注+结构物化段。
- Indexing：ck_external_call 落行（含 schema 建表并入 v2 init）。
- tests 两侧。

## 影响范围（Impact）

- 受影响能力：cozo-dotnet-codeknowledge（新增 depa-ontology 需求）。
- 受影响代码：cozo-lib-dotnet/src/、llm-wiki packages/{Indexing}、tests。
