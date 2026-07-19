# 设计：确定性业务候选投影

`BusinessOntologyCandidateDeriver` 只读取 `ck_symbol`、`ck_file`、`ck_edge`、`ck_entry_point`、`ck_process`、`ck_doc_block` 和 Spring 角色/路由观察。它按固定、可解释的规则识别候选：

- Java DTO/entity/VO 角色与稳定字段形成概念/属性候选；
- controller route、service 和 transaction 形成 implementation mapping 与 lifecycle/rule candidate；
- 前端 page/form/doc 形成 presentational 或 inferred corroboration；
- 业务 FQN 从显式 ontology ID + 规范化候选名派生，中文说明来自源码/文档可用文本或固定模板。

所有候选带 `pending` review state；可导出对象仅在有足够 evidence 时写为 `hypothesis`。接受决策仍由 review 或后续人工/LLM projection 处理。
