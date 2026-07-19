# 变更：从 CodeKnowledge 推导业务本体候选

## 目标

在独立 `onto_*` 存储之上，读取 `ck_*`、Java/Spring 和前端实现观察，生成带 repository-relative evidence 的业务概念、属性、映射和工作流候选。

## 边界

- 输入仅为 CodeKnowledge 与直接源码/文档观察；不读取 `depa_*`。
- 候选默认 `pending` 或 `hypothesis`，不把命名和框架角色自动升级为业务真相。
- 不改 XML exporter、CLI、Bun 或 Om.Core。

## 验收

- memory fixture 覆盖 Java DTO/route/service、前端页面和 `depa_*` sentinel。
- 输出写入 `BusinessOntologyStore`，可重复执行且无 `depa_*` 查询依赖。
- 每个候选可追溯到至少一项 direct evidence，输出使用中文 label/description。
