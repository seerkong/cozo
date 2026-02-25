# 方案设计：执行流提取

## 上下文

图现状：ck_symbol（sym_key/kind/visibility/exported）、ck_edge（CALLS conf 1.0/0.9/0.7/0.5，resolver roslyn/treesitter）、ck_community/ck_member（G5-T1）。GitNexus 参照：process-processor（入口点框架检测 → BFS → 动态上限 max(20,min(300,symbolCount/10))、minSteps 3、cross_community 标注）。

## 方案概览

1. **入口点检测**（Om.CodeKnowledge，internal 策略集合，输出写 ck_entry_point）
   - main：kind=method 且 name=Main，或 caller 归属含 `<Main>$`（顶层语句 DocId）。
   - http_route：CALLS 边 evidence/调用点 targetName ∈ {MapGet,MapPost,MapPut,MapDelete,MapMethods}——从 ck_edge 反查这些未消解/外部调用不可行（外部不落边），改从 ParsedCallSite 不可得（Om 层无源码）；MVP 方案：Indexing 层在索引期检测（RepositoryIndexer 已有 CallSites），把 route handler 候选作为 entry point facts 随批写入 ck_entry_point（kind=http_route，handler=同语句 lambda 不可得时记注册所在方法）。
   - mcp_tool：启发式——类名含 ToolRunner/含 "tools/call" 分发的方法（MVP：方法名 CallAsync 且容器类名含 ToolRunner）；记 findings 局限。
   - public_api：exported=true 且 visibility=public 的方法/构造，仅当 EntryPointModes 含 public_api（默认含，但 process 预算会约束产出）；public_api 入口的 process 优先级最低（预算截断先弃）。
   - cli_command：MVP 并入 main 可达链，不单独检测（记 findings）。
2. **Process 遍历**（Om.CodeKnowledge public API）
   - `ExtractProcessesAsync(ProcessExtractionOptions?)` → `ProcessExtractionResult`；options：MinConfidence=0.7、MinSteps=3、MaxStepsPerProcess=50、MaxProcesses=null（默认公式）、EntryPointKinds。
   - 每入口：BFS over call_graph（Datalog 投影拉边到 .NET 遍历；visited 去环；步骤=首次到达顺序，via_kind=CALLS）；步骤含入口自身（step 0）。
   - process_type：入口 kind；若步骤成员社群（ck_member）≥2 种 → 追加 ",cross_community"。
   - name：入口符号名 + 文件短名。
   - 排序与确定性：入口按（kind 优先级 main>http_route>mcp_tool>public_api，symbolId asc）；MaxProcesses 截断从 public_api 尾部起。
   - :replace 全量重建 ck_entry_point/ck_process/ck_process_step。
   - 查询 API：ListProcessesAsync、GetProcessStepsAsync、FindProcessesForSymbolAsync（P1 mission impact 富化会用）。
3. **管线集成**（llm-wiki）
   - Indexing：索引尾部（社群后）按 ExtractProcesses 开关调用；http_route/mcp_tool 入口候选由 Indexing 在批构建期从 CallSites 检测并写入 ck_entry_point 候选（entry facts 进 batch），Om 层 ExtractProcesses 消费已有 ck_entry_point + 自检 main/public_api。
   - 失败 catch → process_failed 诊断；Summary add-only：EntryPoints/Processes/DroppedProcesses。
4. **dogfood**：本仓 + llm-wiki 仓（有真实 MapGet/MapPost 与 ToolRunner）跑提取，数量/分布/抽样评注记 findings。

## 影响范围与修改点（Impact）

- Om.CodeKnowledge：ProcessExtraction（新文件）+ models（options/result/summaries public，检测器 internal）。
- Indexing：入口候选检测 + 管线接线；Core add-only；CodeKnowledgeBatch 增加 EntryPoints 列表（add-only）。
- tests 两侧。

## 决策摘要

- 入口检测分层：语法级候选（Indexing，有源码）+ 图级候选（Om，main/public_api）——各自就近取证。
- 预算截断顺序：public_api 最先弃（价值密度最低）。

## 风险 / 权衡

- mcp_tool/route 启发式误报 → metadata 记 evidence，minSteps 过滤垃圾流。
- public_api 入口爆炸 → 预算 + 截断顺序。
- BFS 大扇出 → MaxStepsPerProcess 截断 + visited。

## 最小公开面

public：ExtractProcessesAsync + 3 查询 + options/result/summary records + CodeEntryPointFact（batch 契约）。检测器/遍历器 internal。
