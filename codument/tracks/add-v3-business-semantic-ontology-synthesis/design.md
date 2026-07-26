# Design: v3 业务语义本体归纳

## 核心判定

v3 的输入可以来自 `ck_*`、`depa_*`、`judgment`、`CodeAnchor` 和基础 `onto_*` 投影，但它们都是证据或导航索引，**不是**业务本体的直接类型表。

一个 v3 概念只有在模型把多个实现事实归并为稳定业务含义时才成立。概念 ID 使用项目业务命名空间，例如 `ItAssetManagement.Asset`；实现类、DTO、路由和页面以 mapping/anchor 身份存在，不能成为 ID 的替代品。单锚点或 FQN 同名候选须作为 gap、mapping 或待补证诊断。

## 修订后的责任边界

业务语义理解不是本仓库的职责。本仓库拥有索引后的事实、证据解析、安全边界和 CLI 协议；外部分析工具拥有 prompt、模型、检索策略、上下文拼装、agent loop、候选本体和审核工作流。因而本仓库不会再以 v3 completion 输出代表业务理解。

既有 `run_business_semantic_synthesis` / `publish_business_semantic_synthesis` 保留为兼容实现，不作为新集成推荐接口，也不再在本 track 中修改其提示词或继续真实模型实验。删除它们属于破坏性 API 变更，需要独立批准。

## Investigation CLI

CLI 的规范入口为 `depa-wiki investigate <resource> <action>`。路径是可扩展的命名空间，而非暴露数据库查询：例如 `domains discover`、`use-cases list`、`state-rules find`、`implementations find`、`patterns find`、`evidence get`；未来可增加更深路径，例如 `ontology subjects inspect`，而不会改变既有路径。

每个路由只映射到既有、白名单、只读且有界的 `BusinessOntologyInvestigationService` 操作。CLI 参数表现为 HTTP query params：`--term asset --limit 10`。对象型输入表现为 HTTP JSON body：`--json '{...}'`，或 `--json -` 从 stdin 接收完整多行 JSON 对象。若 body 与 flag 给出同名字段，显式 flag 覆盖 body；storage 参数不会传入工具。body 必须为一个受限大小的 JSON object，不能是 SQL、prompt 或任意路径读取协议。

外部工具自行按需多次调用 CLI、读取返回的 evidence pack（其中带已索引源码摘录），再执行自己的模型推理。每个 CLI 调用独立、可审计且无模型 provider 依赖。

### 领域拓扑

`investigate topology domain --term <literal>` 返回一个只读、可视化消费的 evidence graph。图从一个显式术语根开始，按 `domain -> entry-point -> subject -> semantic-claim -> evidence` 组织；每个节点带稳定 ID、层级、类型、入/出连接数和 `weight`。`weight` 是图中可解释连接度加上直接语义观察数，只用于消费者映射节点大小，不能被解释为业务重要性或置信度。

拓扑的边只来自 `ck_entry_point`、受限 CALLS 路径和 `ck_semantic_claim` 的已索引证据。输出受节点、边和每层条目上限约束并稳定排序：最多 20 个入口、160 个 subject、240 个 claim、160 条 CALLS 边，总边数最多 680；达到任一上限时 `truncated=true`。它不读取任意路径、不会写入数据库、不会产生 ontology candidate；外部分析器应将拓扑作为从全局入口向下选择证据的导航面。

## 原运行形态（兼容实现）

每个业务域使用一个有界工作项，顺序如下：

1. **Discover**：通过术语、入口、数据对象、枚举、调用和前后端对应关系提出 domain charter。charter 包括候选业务域、参与者、术语、疑似流程、已知边界和最初证据。
2. **Explore**：围绕 charter 主动调用现有白名单调查工具；必要时补充领域化聚合查询，例如跨层概念簇、端到端用例路径、状态/校验模式和数据所有权证据。模型无任意 SQL、任意路径或源码树访问权。
3. **Synthesize**：由 modeler 产出业务概念、属性、关系、规则、生命周期和实施 mapping。每一项附 evidence refs、所属域、置信度和自然语言推理摘要。
4. **Critique**：独立 critic 仅基于候选、证据摘要和质量合同做 keep/merge/drop/defer 决定，重点检查一对一符号复制、同义重复、无证据语义、前后端冲突及不可证明状态迁移。
5. **Publish**：将通过 critic 的内容写入 pending `semantic-candidate.xml`，其余内容写入 diagnosis/review packet。发布不调用 promotion。

运行时通过配置提供数据库副本、候选制品根、Codex provider 与模型；默认模型为 `gpt-5.6-terra`。制品根由调用方选择，仓库不持有部署位置。

## 语义质量门

发布器必须计算并记录以下质量信号：

- **聚合度**：概念拥有的不同实现锚点类别和证据来源；单一代码符号不能单独形成 confirmed semantic type。
- **业务命名**：候选 ID/名称不得是实现 FQN、文件路径或机械去后缀的类名；必须拥有中文业务描述。
- **关系完整性**：关系两端必须是已通过聚合检查的业务概念，并具备调用、数据写入、接口交互、状态/校验或跨端行为等可追溯证据。
- **规则/生命周期完整性**：每条规则或迁移指向验证分支、枚举状态、持久化更新、流程入口或等价证据；只凭名称猜测的规则必须 rejected/deferred。
- **投影差异**：候选概念与基础符号投影进行规范化匹配。若候选集与符号集近乎一对一、概念没有聚合、或所有语义边为空，运行状态为 `semantic_quality_failed`。

质量门不以“候选数量大”作为成功信号。成功是少量或多量业务概念都可解释、可追溯且形成业务关系结构；诚实的失败报告优于制造本体。

## 制品与审核

每个 v3 run 生成：

- `domain-charters.json`：域边界、探索计划和证据摘要；
- `semantic-candidate.xml`：仅 pending 的业务语义模型，含中文描述、code mappings 与证据；
- `review-packet.json`：逐项的 accept/reject/defer/request-evidence 建议与 critic 理由；
- `quality-report.json`：聚合度、投影差异、关系/规则/生命周期覆盖、冲突、预算与失败原因；
- `provenance.json`：模型调用、查询 digest、证据引用、版本、预算和输入指纹。

只有后续明确的人工 review/promotion 命令能将已接受项写入 accepted ontology 并导出 canonical XML。该命令不属于本 track。

## 工程分期

1. 定义 domain charter、semantic cluster、candidate bundle、critic verdict 与质量报告的强类型合同和本地 validator。
2. 为现有 investigation API 增加必要的领域聚合只读查询，并接入 agent action schema。
3. 实现 discover/explore/synthesize/critic/publish 编排、预算、缓存和原子发布。
4. 实现 XML/JSON 导出和 review packet，不复用 canonical exporter 作为语义输出。
5. 以 fake provider fixture 验证语义聚合与负例，再在配置化 copied database 上做一次小预算真实运行。

## 预算与资源安全

默认运行先限制为至多 6 个业务域、每域至多 4 次 completion、每次最多 10 个调查操作；总 token、墙钟、并发和查询结果仍受既有 agent budget 约束。所有模型调用串行执行，超时或取消必须终止子进程树，并在 .NET 校验命令中禁用 MSBuild node reuse。真实运行的上限由 CLI 明确传入，不能由模型提升。
