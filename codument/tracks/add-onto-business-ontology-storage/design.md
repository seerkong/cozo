# 设计：`onto_*` 业务本体存储

## 上下文

业务本体必须与代码/架构观察分层：`ck_*` 给出观察，`onto_*` 保存解释及其证据，XML exporter 再输出可验证的领域声明。由于现有 LlmWiki 已直接使用 Cozo runtime store 建立 package-owned relation family，本实现不需要扩大 OM 内核边界。

## 方案概览

1. 在 Tools 包定义 immutable input/read models 和 `BusinessOntologyStore`。
2. Store 初始化 `onto_generation`、semantic object、evidence/ref、candidate/review relations。
3. 在写入前完成有限值、FQN、路径、引用、hypothesis evidence、generation coherence 校验。
4. 先写/校验新 generation，再 scoped replace 同 ontology 的旧生成行；其他 ontology 和非 `onto_*` relation 不在删除范围。
5. 提供 exportable view：只返回 `accepted` / `hypothesis` 语义对象，且要求完整直接证据；候选与审查仍可单独查询。

## 数据边界

- `onto_evidence` 保存 repository key、relative path、symbol、line range、grade、resolver、confidence 和 source kind。
- `onto_evidence_ref` 把证据连接到概念、属性、关系、规则、生命周期、状态、迁移或映射，不把路径复制到语义表。
- `onto_candidate` 是 proposal payload 与 reason，`onto_review` 是 append-only decision；二者不等于 ontology declaration。
- `onto_*` 不包含或引用 `depa_*` identity。

## 测试与验收

- 先写 memory-db tests，再实现 schema/store。
- fixture 必须包含一个完整可导出语义图与一个 pending/rejected candidate。
- 断言无效 evidence、confidence、FQN、status 和跨 generation 引用失败。
- 用两个 ontology IDs 验证 scoped replacement，并在同一 fixture 放入 `ck_*` / `depa_*` sentinel 断言其不变。

## 风险与缓解

| 风险 | 缓解 |
|---|---|
| relation schema 过早绑定 exporter 细节 | 只存语义和 evidence；XML layout 留给后续 track。 |
| 刷新误删其他项目数据 | 所有删除以 ontology ID + generation owner 双重限定，并有跨 ontology fixture。 |
| 命名启发式被当成规则 | candidate/review 与 hypothesis evidence gate 分离，store 不提供隐式接受路径。 |
| 与 OM parity mission 冲突 | 不写 Om.Core、Bun 或 OM tests；需要通用能力时停止并 replan。 |
