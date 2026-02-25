# 变更：执行流（Process）提取

## 背景和动机 (Context And Why)

mission `deepen-llm-wiki-code-graph` G5 第二弹（最后一块图能力）。调用图（G3/G4）与社群（G5-T1）已就位，还差执行流：从入口点沿 CALLS 遍历得到的"某功能怎么跑"的步骤链（GitNexus 的 Process/STEP_IN_PROCESS 对标项）。这是 agent 理解"改这里影响哪些流程"（P1 mission 的 impact/detect_changes 富化）与 P2 mission workflows 分形文档的输入。

## "要做"和"不做" (Goals / Non-Goals)

**目标:**
- 入口点识别（D5 决策范围）→ ck_entry_point：main（Main 方法/顶层语句）、cli_command（启发式：Main 可达的顶层 switch/子命令方法，MVP 可并入 main 类）、http_route（MapGet/MapPost/MapPut/MapDelete 调用点的 handler）、mcp_tool（工具注册/分发方法，启发式）、public_api（公开导出类型的公开方法，仅当仓库无以上入口时兜底或按开关）。
- Process 遍历：每入口点沿 CALLS（confidence≥0.7）BFS，去环，产出 ck_process（name/entry_symbol_id/entry_kind/process_type/step_count）+ ck_process_step（step 序号 + via_kind）。
- 有界产出（A2）：maxProcesses = max(20, min(300, symbolCount/10))；minSteps=3（不足弃）；maxStepsPerProcess（默认 50，截断记诊断）；process_type 标 cross_community（步骤跨社群时）。
- 管线集成：RepositoryIndexRequest.ExtractProcesses（默认 true），索引尾部（社群之后）重算（:replace）；失败不炸索引。
- dogfood：本仓 Process 数量/规模/入口分布记 findings，抽样人工评注可解释性。

**非目标:**
- 不做数据流/分支敏感遍历（纯调用可达性）。
- 不做异步消息/事件流建模。
- 不新增 agent 面工具（P1 mission）。

## 变更内容（What Changes）

- Om.CodeKnowledge：ProcessExtraction 公开 API（ExtractProcessesAsync + 查询）+ 入口点检测器（internal 策略）。
- llm-wiki Indexing/Core：管线集成 + add-only 开关与计数。
- tests 两侧。

## 影响范围（Impact）

- 受影响能力：cozo-dotnet-codeknowledge（新增 process-extraction 需求）。
- 受影响代码：cozo-lib-dotnet/src/Om.CodeKnowledge/、llm-wiki packages/{Indexing,Core}、tests。
