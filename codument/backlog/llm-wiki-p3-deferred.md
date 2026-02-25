# llm-wiki P3 延后候选（2026-07-06，G5 评估结论）

- **.cozo-wikirc per-repo 配置文件**：CLI 选项持久化便利层；触发条件=多仓使用中出现重复传参痛点。自主度：单 track 可闭环。
- **多仓库 group/Contract Bridge**：跨仓统一图与契约链接；触发条件=出现真实第二仓库/微服务组诉求。自主度：需先做 mission 级证据盘点。
- ~~hooks/MCP 默认接入~~：**已决策并执行（2026-07-06）**——依据 codex 双模式 eval（native−baseline=+0.854，must 7.7%→96.2%，eval/results/2026-07-06-codex/report.md），本仓已 `hooks install`（.claude/settings.json，gitignored 本机生效：PostToolUse augment + SessionStart staleness），cozo-wiki 符号链接进 ~/.local/bin，仓根全量索引（709 文件/5352 符号），两 hook 端到端验证通过。其他仓库按需 `cozo-wiki hooks install --work-dir <repo>`。
- （交接自 P1/P2/P3 各 track lessons：TS ≥0.8 调用边、V-L1 零命中=PASS 张力、增量提速深化 ~5x、OmMutationContext V-F2 整改样本、trace 示例连通对选择。）

## wiki 分形生成 dogfood backlog（2026-07-06）

来源：track `add-docs-and-sop-skills` P2 自举 dogfood（`codument/tracks/add-docs-and-sop-skills/analysis/findings.md` §4，7 条产品缺陷/改进候选，均不就地修）。

- ~~W-1 分形管线无 CLI 一级入口~~：**已完成（2026-07-06，track fix-wiki-fractal-entry-and-context-ranking）**——`cozo-wiki wiki` 增加 `--pipeline legacy|codument-fractal`（legacy 默认零回归，未知值 exit 2 报错）；`build_wiki` 规范名 codument-fractal、`fractal` 留静默兼容别名，结果 JSON 统一回显 codument-fractal；手册与 modeling docs 同步。
- ~~W-2 MaxModelingContexts=24 丢弃不按重要性排序~~：**已完成（2026-07-06，track fix-wiki-fractal-entry-and-context-ranking）**——预算截断改按重要性排序（public-API 成员数降 → 总成员数降 → 名称升，Ordinal 确定性 tiebreak），diagnostics 注明排序依据；本仓复扫入选从 6 个 .NET namespace + 18 个 JS demo 函数级微社群 → 20 个 .NET namespace/类级 context（Om.Depa.DepaViolationDetectors、LlmWiki.Tools.LlmWikiToolRunner、Datalog、SemanticParsing、VectorSearch 等入选），public=0 的 demo 函数微社群全部沉底；Om.CodeKnowledge 侧两个扩展类 context 与 Om.Depa.DepaScanPipeline 仍在 24 席之外（public 密度不敌入选者，详见 track findings）。misc 归并容器与微社群折叠留 W-3。
- **W-3 context 粒度混杂**：函数级社群与 namespace 级社群并列为 context，类目不正交；社群聚合应折叠微社群或按包边界归并。
- **W-4 MaxWorkflowsPerContext=10 截断不分页**：Cozo.DotNet 88 条 workflow 丢失，仅 diagnostics 一行提示。
- **W-5 生成语义质量**：Not Owned Here 机械枚举全部兄弟 context（23 行噪音）、Boundary 为符号计数统计句；待 LLM 层或启发式改进。
- **W-6 howto 叶子名实不符**：`working-with-X.md` 内容实为入口点 reference 表，类目→内容语义映射错位（checker 只查 doc_role 与格式，查不出名实不符）。
- **W-7 结果 JSON 全量打 stdout**：`cozo-wiki wiki`/`call build_wiki` 把 3.2MB 结果 JSON 打到 stdout，无 --quiet/摘要模式，agent/管道场景不友好。

- **W-8 eval-oracle 对 doc 语料稀释敏感**：用户手册（cozo-lib-dotnet-llm-wiki/docs/ 8 文件）进入 eval fixture 索引后，semantic_search top-5 被 doc 命中挤占，eval-001 must 1/2、eval-009 must 0/1（aggregate 26/26→24/26=92.3%，门禁仍绿）。已在 worktree@796e74c2 复跑归因确认与 codument-fractal 代码改动无关。修复方向：eval 任务加 sourceKinds=code/symbol 约束、或 semantic_search 排序对 symbol 命中加权、或 oracle 提高 limit。触发条件：下次 eval 体系或搜索排序切片。

## depa-expert 盘点 backlog（2026-07-06）

来源：track `fix-om-depa-conformance-gaps` 的 depa-expert 模块级盘点（分析工作区 `codument/tracks/fix-om-depa-conformance-gaps/analysis/depa-scan-workspace/recommendations/backlog.md`），T3.1 收口时登记。

- **B-1 AddInterceptorAsync 内存先于 db 漂移**：handler 重载先 `Runtime.Registry.RegisterInterceptor`（拿 seq）再写 db，db 失败时内存已注册（内存/db 漂移，非幂等）；而 DefineActionAsync/DefineMutationAsync 是 db 先、内存后（cozo-lib-dotnet/src/Om.Core/CozoOm.cs:165-197，RegisterInterceptor 在 :196）——两处顺序纪律不一致。触发条件：下次 Om.Core 一致性小切片，统一"db 成功后才登记内存"。
- **B-2 depa: 前缀撞名防护缺失**：depa 派生实体与用户业务实体同居 om_entity，仅靠 `depa:` 前缀软隔离（cozo-lib-dotnet/src/Om.Depa/DepaScanPipeline.cs:68-96 UpsertAsync 构 id），用户实体撞前缀无防护。触发条件：InitDepaOntologyAsync 文档声明保留前缀或加存量校验时。
- ~~B-3 工具侧 V-F2 纯委托豁免~~：**已完成（2026-07-06，track refine-depa-detection-precision）**——DetectF2 豁免"仅 1 条出向 CALLS 且无 write ACCESSES"的纯委托形态（弱化口径记 rule-map F4 行 implemented-weakened + decisions.md）；同 track 一并根治 CallResolver 兜底 name-only 回退缺陷（跨文件 private/protected 剔除 + arity 双知校验），本仓复扫 ambiguous CALLS 边 9→0、V-P2 GAP 9→0（根因修复非压制），unresolved 47.3%→55.9% 如实记录。OmMutationContext 形态无 depa-map 标注也不再误报。
- **B-4 scan 逐属性写无批量**：DepaScanPipeline 每实体多次 SetPropertyAsync 逐条 RunAsync，无事务批量（cozo-lib-dotnet/src/Om.Depa/DepaScanPipeline.cs:68-96），大仓扫描往返放大。触发条件：大仓 scan 计时出现瓶颈时做性能切片（scan 段套事务/批量 put）。
- **B-5 Support/ 混装**：Om.Core Support/ 同时装 effect 实现 CozoDbOmStore（cozo-lib-dotnet/src/Om.Core/Support/CozoDbOmStore.cs:7）与纯 helper CozoScriptBuilder（cozo-lib-dotnet/src/Om.Core/Support/CozoScriptBuilder.cs:3），目录语义可再分（Effects/ vs Support/）。美化级，触发条件：下次 Om.Core 结构调整顺带。
