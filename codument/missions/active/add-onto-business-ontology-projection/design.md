# Mission Design: add-onto-business-ontology-projection

## 控制论模型

- desired state: `mission.xml` 中的事实/契约收敛、`onto_*` 存储、业务语义投影、XML 导出和真实项目验证 DAG。
- actual state: `.depa-wiki` 的 `ck_*`/框架派生事实、源码锚点、当前 DEPA-only exporter、下属 track 状态、测试与真实项目导出报告。
- actuation: 创建、执行、验证与归档下属 tracks；只有 evidence 或用户决策出现时才修订 mission。
- feedback / drift: 业务候选质量不足、证据不能定位、旧 exporter 的兼容冲突、真实项目结果空洞或过度代码化、共享 OM 内核边界冲突。

## Mission Actors

| Actor | 控制论角色 | DEPA 归属 | 职责 |
|---|---|---|---|
| MissionPlanner | 期望态产出者 | Processor + Actor | 将业务本体目标拆为 storage、derivation、export、verification tracks，并在证据不足时受控重规划。 |
| MissionObserver | 传感器 | Data + Actor | 读取 CodeKnowledge 事实、源码锚点、`onto_*` 状态、导出 XML、测试与真实仓库结果。 |
| MissionReconciler | 控制器 | Processor + Actor | 对比期望的业务语义质量与实际产物，判断 ready、drift、blocked 或 done。 |
| MissionApplier | 执行器 | Effect + Actor | 创建/续跑一个 track、执行验证、落 report，或在越界时提出协调性重规划。 |

## Plan 与 Track 的边界

本 mission 只管理目标、顺序、端口、风险与收口门禁。下列真实交付必须由 track 完成：

- `onto_*` schema、读写服务、清理/幂等策略与迁移测试；
- 从代码事实到业务候选、确认本体与 evidence 的语义投影；
- XML writer、CLI/API、格式兼容迁移和中文说明；
- fixture、单元测试、两个真实数据库的端到端验证及导出报告。

## 语义边界

```text
源码 / CK 结构事实 / 框架派生事实
                |
                | 仅作为可追溯 evidence
                v
业务候选与审查状态 --受控语义投影--> onto_* 已确认业务事实
                                               |
                                               v
                                  标准业务本体 XML（中文说明）

depa_* 架构判断: 独立生成、独立演进；本阶段不读作前置、不写双向标注。
```

`onto_*` 的第一版最低语义覆盖应包括：

| 语义 | 存储责任 |
|---|---|
| 业务概念与分类 | `onto_concept` 或等价实体 |
| 属性与值类型/必填性 | `onto_attribute` |
| 业务关系及基数/方向 | `onto_relation` |
| 业务规则、前提与结果 | `onto_rule` |
| 生命周期、状态、迁移 | `onto_lifecycle`、`onto_state`、`onto_transition` |
| 代码实现定位 | `onto_mapping` |
| 置信度、来源、源码证据 | `onto_evidence` |
| 候选与人工审查工作流 | `onto_candidate`、`onto_review` 或等价状态模型 |

表名是持久化元模型，不是导出的业务词汇。导出 XML 必须使用至少两段的项目与领域命名空间，例如 `ItAssetManagement.Ontology.Asset`，不能把 `onto_concept`、`ck_symbol` 或 `depa_processor` 输出为领域概念。

## Track DAG 与并行纪律

1. `add-onto-business-ontology-storage`：锁定元模型、来源/证据纪律和读写/清理合同。
2. `add-onto-business-ontology-derivation`：在 storage contract 上实现候选与已确认业务事实的语义投影。
3. `add-onto-business-ontology-xml-export`：从 `onto_*` 读取并输出 XML；处理旧 DEPA-only `ontology-xml` 的兼容迁移。
4. `verify-onto-business-ontology-projection`：以 fixtures 和两个真实项目数据库验证完整链路与输出质量。

这些 tracks 与 `converge-dotnet-om-bun-capability-parity` 可并行，但本 mission 的实现端口限于 LlmWiki：

- 可写：`cozo-lib-dotnet-llm-wiki/packages/**`、`cozo-lib-dotnet-llm-wiki/tests/**`、其 CLI 文档和本 mission/下属 tracks。
- 禁止写：`cozo-lib-bun/**`、`cozo-lib-dotnet/src/Om.Core/**`、`cozo-lib-dotnet/tests/**`。
- `Cozo.DotNet.LlmWiki.McpServer/Program.cs` 当前已有未提交修改；CLI integration track 必须先检查文件归属和最新内容，采用单一 writer，不得覆盖并发工作。

如 storage contract 证明必须扩展通用 OM API，MissionReconciler 必须停止该叶子任务、写 replan report，并新建显式协调 track；禁止在本 mission 内顺手修改 parity mission 的文件。

## 质量门禁

1. Fact boundary gate：导出业务 XML 不出现 `ck_*` 或 `depa_*` 作为领域节点。
2. Provenance gate：每项已确认业务语义都能定位回至少一个源码/索引 evidence，并带来源与置信度。
3. Determinism gate：相同输入重复投影结果稳定，重跑能替换陈旧投影而非重复累积。
4. Semantic gate：fixture 必须覆盖类型、属性、关系、规则、生命周期和候选/确认差异。
5. XML gate：输出符合既有 ontology XML DSL 的层次与校验约束，解释文字为中文。
6. Dogfood gate：`is-asset-new` 与 `is-asset-fe` 的已索引库都产生非空、可阅读且跨前后端可区分的业务本体；结果必须做抽样人工复核并写报告。
7. Regression gate：现有 DEPA runtime snapshot 与 wiki 导出行为保持可验证；任何 CLI 命名/格式迁移均有兼容或清晰弃用策略。

## 受控重规划与人工介入

以下情形必须写 `reports/replan-*.md` 并更新 mission revision：

- 目标数据库缺少足够的索引/框架事实，无法形成可审计候选；
- 现有 XML DSL 无法表达必要业务规则或生命周期语义；
- 旧 `ontology-xml` 调用者需要不兼容迁移；
- 候选噪声或错误合并达到无法用固定门禁控制的程度；
- 发现必须修改 OM 内核或与 parity mission 共享的文件。

人工介入用于审查业务语义抽样和选择需要提升为“确认”的候选；不用于替代证据记录。
