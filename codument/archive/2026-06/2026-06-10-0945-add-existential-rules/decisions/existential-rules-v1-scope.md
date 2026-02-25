# 存在规则子系统的定位与 v1 范围

**Durable / 长期项目决策**

## 决策
1. **head 形态**：存在规则的 head 仅表达「∃ 关系边 + 目标实体」（`exists: { rel, direction?, toType }`）。属性存在性（每个 X 必须有属性 P）由 `required` 属性定义 + `validateConstraints` 承载，**不在存在规则子系统中重复建设**。
2. **与 constraint 层的分工**：
   - constraint 层（`defineConstraint` / `validateConstraints`）：逐实体、JS 回调、写入时/单实体校验定位
   - 存在规则子系统（`om_existential_rule_def` + check/apply API）：集合语义、声明式 JSON、批量治理与物化定位
   - 两者平行共存，不合并
3. **不改 CozoDB 内核**：存在规则全部实现在 cozo-om 应用层（Skolem 改写 + 宿主语言驱动循环 + 违例检测查询），不修改 cozo-core 查询求值器。

## 理由
- 关系边存在性覆盖主数据场景中绝大多数存在约束；引擎级 chase（restricted chase 语义）是研究级改造且会形成永久 fork
- 声明式 JSON spec 可持久化、可进 schema snapshot、可 diff/回滚，JS 回调做不到
- 真正需要完整 restricted chase 语义的场景，未来通过导出事实外包给 Nemo 跑批（不在 v1）

## 约束后续工作
- 后续扩展 head 形态（如 ∃ 属性、复合 head）需先修订本决策
- 任何新增的 schema 元数据表都必须同步纳入 `_readSchemaSnapshotParts` 的 snapshot 范围
