# Mission 设计：AI 主动业务本体重建

## 目标系统

系统分为四层，并具有明确的信任边界：

1. **事实层**：索引后的代码知识、DEPA 观察、judgment、锚点以及当前 `onto_*` 投影。它们是可查询输入，不是业务真相。
2. **调查层**：受分页和预算限制的只读工具返回业务领域视图，例如概念邻域、请求流程、状态写入、校验 guard、前后端佐证、未映射对象和本体冲突。模型不会获得任意 SQL 或仓库访问权。
3. **分析层**：不可变的运行 provenance，加上 `observation`、`hypothesis`、`conflict`、`gap`、`candidate` 的可审核状态。每项都记录证据 ID、查询/结果摘要和不确定性；它绝不是 accepted ontology store。
4. **建模与治理层**：候选 XML bundle 复用 `ontology-xml-dsl`；诊断 bundle 解释继承的模型缺陷。只有本地 validator 与显式 review/promotion 才决定哪些内容可以进入 accepted `onto_*`。

## Agent 循环

skill 执行的是有界循环，而不是一次 completion：

1. 根据本体覆盖、路由/use-case slice 与已检测到的不一致建立领域工作清单。
2. 为一个工作项查询紧凑概览。
3. 形成有证据支撑的观察与范围很窄的假设。
4. 仅检索证明、反驳或降级该假设所缺少的邻域。
5. 与既有本体和跨端证据协调；证据不足时记录 conflict/gap，而不是强造候选。
6. 仅从已验证假设生成候选 XML 项，并运行本地 DSL、身份、证据和状态校验。
7. 输出审核 bundle，并在配置的成本/查询预算处停止。

agent 可以声明“未知”并生成 gap；绝不能为了增加产出量而把不确定性转换成 type、property、relation、rule 或 lifecycle。

## Mission Actors

| Actor | 控制论角色 | DEPA 归属 | 职责 |
|---|---|---|---|
| MissionPlanner | 期望态产出者 | Processor + Actor | 维护 mission DAG，并在 v2 证据出现后提出有界重规划 |
| MissionObserver | 传感器 | Data + Actor | 读取 mission/track 状态、查询合同、运行报告和审核结果 |
| MissionReconciler | 控制器 | Processor + Actor | 以实际运行指标对比预期覆盖和治理保证 |
| MissionApplier | 执行器 | Effect + Actor | 创建/执行下一条 linked track，或写入重规划/人工介入报告 |

## Track 计划

| 分组 | 候选 track | 交付物 | 门禁 |
|---|---|---|---|
| G1 | `add-onto-investigation-query-contracts` | 只读领域查询 API，具备预算、分页、源码锚点上限和 provenance | 不泄露任意 SQL 或源码根路径 |
| G2 | `add-onto-agentic-analysis-workspace` | 与 accepted ontology 分离的分析运行/状态模型 | 候选/审核隔离，可重放 provenance |
| G3 | `add-agentic-business-ontology-reconstruction-skill` | explore/verify/reconcile/model 的 skill 与编排器 | 无无界循环；模型读取全部来自 G1 工具 |
| G4 | `add-ontology-diagnosis-and-candidate-export` | DSL 候选导出与 conflict/gap/遗留模型诊断报告 | XML 可校验；诊断不得伪装为 accepted ontology |
| G5 | `verify-agentic-business-ontology-reconstruction-v2` | 两仓库 copied-DB v2 bundle 和审核报告 | 可测量的价值/成本；不自动提升 |

## 候选与诊断输出

候选 XML 仍是规范的本体表达，并使用 `ontology-xml-dsl` 已定义的三层范式。配套诊断报告必须保持独立 namespace/state，至少包括：

- 重复或含混的概念与映射；
- 缺失的 type/property/relation/rule/lifecycle 假设；
- 前后端命名、路由或状态不一致；
- 不受支持的历史候选与相互冲突的证据；
- 尚未证实的假设及其精确缺失证据。

## 预算与安全

- 单次运行限制 agent turn、查询次数、行数、源码字节数、并发调查数、模型 token 与墙钟时间。
- 查询输出是类型化、分页、确定性且记录 digest 的；源码片段保持仓库相对路径并限制字节数。
- 每个模型响应都是不可信输入。本地 validator 拒绝未知 ID、开放 schema 字段、不支持的状态变更和缺失证据。
- 原始模型输出不导出为证据；provenance 仅保存有界 digest 和结构化、通过校验的结果。
- 首次真实运行是 copied-database v2 实验；提升是单独、人工介入的操作。

## 重规划规则

仅当运行报告或用户决定表明以下任一情况时，mission 才可重规划：查询覆盖不足、反复出现无效假设模式、预算耗尽、不可接受的误报、新的事实源边界，或审核结果改变 ontology DSL 的需求。重规划必须增加 mission revision，并记录触发因素、观测状态、建议的 DAG 变更及必要的人类决定。
