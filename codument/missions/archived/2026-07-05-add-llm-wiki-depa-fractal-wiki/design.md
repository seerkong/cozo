# Mission Design：add-llm-wiki-depa-fractal-wiki

## 控制论模型

- desired state：mission.xml DAG（LLM wiki 基建 → engineering 分形 → modeling 分形；并行线：DEPA 本体 → DEPA 工具），叶子 `cdt:TrackLink`。
- actual state：P0/P1 mission 产出、wiki/工具面代码现状、生成文档的规范检查结果。
- actuation：创建/续跑/归档 track；受控 replan。
- feedback：分形规范 lint、DEPA 判定抽样、LLM 生成质量评审。

## Mission Actors

| Actor | 控制论角色 | DEPA 归属 | 职责 |
|---|---|---|---|
| MissionPlanner | 期望态产出者 | Processor + Actor | 产出/修订 desired graph（两条愿景线的批次切片） |
| MissionObserver | 传感器 | Data + Actor | 读取图产出/文档规范检查/工具判定的 actual state |
| MissionReconciler | 控制器 | Processor + Actor | 判定 drift / ready / blocked（P0 前置门、LLM 质量门） |
| MissionApplier | 执行器 | Effect + Actor | bounded action |

## plan vs track

mission 只做控制面。LLM 管线、分形生成器、OM 本体 schema、DEPA 工具全部由 track 落地。

## 关键设计约束

1. **图先于 LLM**：LLM 只负责叙述与归组辅助；结构性内容（code-map/reference/derived_from/职责块）必须从图确定性生成，可重复、可 diff。
2. **分形规范即验收**：生成器输出必须过 folder-manifest 补齐检查与 model-driven-docs frontmatter 受控字段检查——规范文件本身作为测试夹具。
3. **DEPA 判定纪律**：违反信号必带 path:line；无证据转 backlog 不断言；符合度只判 PASS/GAP/BLOCKED。
4. **OM 承载 DEPA 本体**：DEPA 概念（capsule/contract/reducer/projection/fact-grade…）用 OM Type + existential rule 建模，这是相对 GitNexus 的独特资产，判定结果作为图事实可查询。
5. **LLM 后端抽象**：多 provider（对齐 GitNexus：OpenAI/Anthropic/OpenRouter/本地），契约 + 实现分离，离线可降级（跳过叙述层只出结构层）。
6. **增量**：git diff → 受影响 context/module → 只重生成对应页；.meta 缓存归组结果。

## 决策 frontier（进入 active 时收敛）

- LLM 归组 vs 纯社群检测归组的边界（context 划分谁说了算）。
- 生成文档落盘位置：目标仓库 docs/ 直写 vs .cozo-wiki 下预览再同步。
- effect API 白名单的来源与维护方式（内置 BCL 清单 vs 用户可扩展配置）。
- health_score 的度量口径（各维 GAP 加权？趋势如何存储）。

## 受控重规划 / 风险

| 风险 | 缓解 |
|---|---|
| context 自动划分与业务边界不符 | 归组结果先出 review 稿（对齐 GitNexus reviewOnly 模式），人工可改后固化 |
| DEPA 静态判定误报 | confidence 分级 + 证据必附 + 已知合规/违规样本回归集 |
| LLM 生成漂移 | 结构层确定性生成兜底；叙述层带 last_verified 与再生成命令 |
| 愿景过大收不了口 | engineering 分形先行（LLM 依赖最少）；DEPA 先做 effect 泄漏单点打穿 |
