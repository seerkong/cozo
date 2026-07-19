# Mission Design：add-dotnet-llm-wiki-java-spring-semantic-parsing

## 控制论模型

- desired state：`mission.xml` 中的证据盘点、Java baseline、调用解析、Spring 推导、真实仓库验证 DAG；每个实现叶子任务绑定一个未来真实 track。
- actual state：SemanticParsing/Indexing 当前代码、native runtime assets、Java fixtures、现有 C#/TS/JS 回归结果、目标 Java 仓库索引统计、mission reports。
- actuation：创建/绑定/执行/验证/归档 tracks，或根据 evidence 受控修订 mission DAG。
- feedback / drift：grammar 构建失败、ABI 不兼容、Java fixture 抽取缺口、调用边抽样错误、Spring annotation 变体、真实仓库统计和用户新边界。

## Mission Actors

| Actor | 控制论角色 | DEPA 归属 | 职责 |
|---|---|---|---|
| `MissionPlanner` | 期望态产出者 | Processor + Actor | 产出 Java parser、Spring semantics 和验证的 desired DAG，必要时修订切片 |
| `MissionObserver` | 传感器 | Data + Actor | 读取源码、现有 parser、grammar assets、tracks、测试和真实索引结果 |
| `MissionReconciler` | 控制器 | Processor + Actor | 对比 desired/actual，判断 ready、drift、blocked、done |
| `MissionApplier` | 执行器 | Effect + Actor | 执行一个有界动作：建 track、续跑 track、运行验证、记录 report 或提出 replan |

## Plan 与 Track 的边界

本 mission 只承担：

- 跨 track 的目标和依赖编排；
- Java grammar/框架语义的阶段边界；
- 失败证据和重规划；
- 真实仓库端到端验证的收口。

以下工作必须由真实 track 承担：

- C# 代码和 native grammar assets；
- Java extractor、call resolver、Spring semantic derivation；
- 单元测试、fixture、行为 delta 和文档；
- 目标仓库的实际索引执行与验证脚本。

本 mission 不直接改代码。

## 技术分层

```text
Tree-sitter Java grammar
        |
Java parser backend contract
        |
JavaExtractor: symbols + structural edges + call sites
        |
RepositoryIndexer / CallResolver
        |
ck_symbol / ck_edge / ck_entry_point / ck_process
        |
SpringSemanticDeriver
        |
framework roles, routes, transactions, handlers, evidence
```

Spring 派生层只能消费 Java 图事实和源码文本，不应把 Spring 识别逻辑塞进通用 Tree-sitter extractor。这样可以保持：

- parser 负责语法事实；
- resolver 负责符号关系；
- framework deriver 负责框架解释；
- CodeKnowledge schema 负责事实持久化；
- 下游本体分析负责业务概念归纳。

## Java baseline 语义范围

`JavaExtractor` 首批应覆盖：

- `package_declaration`、`import_declaration`；
- class/interface/enum/annotation declaration；
- method/constructor/field；
- nested parent relationship；
- extends/implements；
- method invocation、object creation、member access；
- JavaDoc 和注解位置；
- `sym_key`、visibility、signature、line range。

`CallResolver` 复用现有 registry/confidence/evidence 纪律。Java baseline 无法确认目标时，只保留 call site 诊断或低置信候选，不凭名称静默建立错误边。

## Spring semantic vocabulary

第一版固定以下角色和关系：

| Derived concept | Evidence examples | Output meaning |
|---|---|---|
| `spring_controller` | `@Controller`, `@RestController` | HTTP/API boundary |
| `spring_route` | `@RequestMapping`, `@GetMapping`, `@PostMapping`, etc. | route + HTTP method/path |
| `spring_service` | `@Service`, `@Component` | application service boundary |
| `spring_repository` | `@Repository`, repository/mapper conventions | persistence boundary |
| `spring_transaction` | `@Transactional` | transaction scope |
| `spring_listener` | `@EventListener`, Kafka/listener conventions | event consumer |
| `spring_handler` | handler naming/registration and known event annotations | event/process handler |
| `spring_dto` / `spring_entity` / `spring_vo` | annotation/name/package conventions | data projection role |

所有派生事实必须携带：source symbol/path、line、annotation or naming evidence、resolver、confidence。规则不应把“命名猜测”提升为后端权威业务规则。

## 质量门禁

1. Grammar gate：native library 可加载、ABI 合法、无 grammar 时诊断明确。
2. Extraction gate：Java fixture 的类型、成员、结构边、import 都有精确断言。
3. Resolution gate：同文件、跨文件、构造调用、歧义调用、外部调用都有 fixture；高置信边抽样正确率达到 track 设定门槛。
4. Spring gate：Controller route、Service、Repository、Transactional、Listener/Handler、DTO/Entity fixture 均可查询并带 evidence。
5. Regression gate：C#/TS/JS 相关测试和真实索引摘要不回归。
6. Dogfood gate：`is-asset-new` 能产生 Java symbols/relations/entry points/processes；Java 语义结果可通过 `overview_graph`、`symbol_context`、`trace` 或等价工具定位到文件行。

## 受控重规划

active mission 期间只在有 evidence 或用户决策时重规划，并写入 `reports/replan-XXX.md`：

- `tree-sitter-java` grammar 无法满足关键 Java 语法时，调整 extractor 范围或增加兼容路径；
- Java 调用解析质量不足时，保留结构 baseline，拆出可选 semantic enhancer；
- Spring 注解变体过多时，先固定核心 vocabulary，把扩展放入后续 track；
- 真实仓库因生成代码或依赖目录造成图膨胀时，调整 include/exclude/预算，不改变核心事实语义。

## 人工介入点

- Java grammar pin 和支持平台确认；
- Spring 首批 annotation vocabulary 的边界确认；
- 真实仓库高置信调用边和 route/transaction 派生事实抽样；
- 任何将“推断规则”提升为“业务权威规则”的决定。

## 风险与缓解

| 风险 | 缓解 |
|---|---|
| grammar ABI 或平台动态库构建失败 | 复用现有 pin/ABI 检查；先保留结构化诊断和 regex 降级；把 platform matrix 单独纳入 track |
| Java 语法覆盖不足 | fixture 驱动 extractor；对 record、annotation、lambda、匿名类等逐项标记覆盖边界 |
| 调用解析误连 | 复用 confidence/resolver/evidence；无唯一候选时不落高置信边 |
| Spring 约定过多 | 只实现固定 vocabulary；所有派生结果保留 provenance 和 confidence |
| 企业 Maven 依赖不可用 | 不把完整编译作为硬前置；结构 baseline 独立成功，semantic enhancer 可选 |
| 图膨胀 | 沿用每文件访问边、过程提取和 relation budget；排除生成目录和依赖目录 |
| C#/TS/JS 回归 | 每个 Java track 都必须运行既有 parser/index tests，新增能力采用 add-only schema 事实 |
