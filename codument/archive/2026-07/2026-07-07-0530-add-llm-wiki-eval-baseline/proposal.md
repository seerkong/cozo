# 变更：轻量 eval 基准（任务集 + 评分器 + oracle 验证）

## 背景和动机 (Context And Why)

mission `harden-llm-wiki-engineering`（P3）G4。GitNexus 用 SWE-bench（baseline/native/native_augment 三模式）量化工具对 agent 解题率的贡献；mission design 约束 4 要求"eval 从小做起：先自建 10–20 个本仓任务对比集，验证方法论"，且 hooks 默认开启决策依赖 eval 数据。真实 LLM agent 运行需要 API key 与成本预算（使用期动作），本 track 交付**可自主验证的 harness**：任务集、评分器、可插拔运行器，以及 oracle 模式（用 cozo-wiki 工具直接采集证据回答任务）证明两件事——任务集可被图回答、评分器可判分。

## "要做"和"不做" (Goals / Non-Goals)

**目标:**
- 任务集：`eval/tasks.json`（llm-wiki 仓 eval/ 目录）≥20 题本仓真实问题（如"谁调用 X""改 Y 影响哪些流程""Z 在哪个 context"），每题含 question、期望答案要素（关键符号/文件/行为要点关键词）、建议工具序列、难度标签。
- 评分器：`eval/score`（.NET console 或 tests 内 runner）——答案文本对要素的加权命中（must/should 两级），输出每题分与总分。
- oracle 模式：对每题执行建议工具序列（LlmWikiToolRunner 直调）拼接输出作为"答案"→ 评分——全集 must 要素命中率 ≥90% 为门禁（证明任务可被图回答；不足则修任务或工具序列）。
- 运行器骨架：`eval/run` 说明 + 可插拔 agent 命令位（baseline=无工具 prompt / native=带 MCP 工具说明 prompt 的两模式模板）；真实 agent 对比运行步骤写 README（使用期动作，不在本 track 执行）。
- dogfood：oracle 全集得分记 findings。

**非目标:**
- 不跑真实 LLM agent（无 key/成本边界；README 给出运行步骤）。
- 不接 SWE-bench（方法论验证后的后续）。

## 变更内容（What Changes）

- llm-wiki 仓新增 eval/ 资产（任务集 + 评分/oracle 逻辑落 tests 或独立小工程，取简单者）。
- tests：oracle 门禁测试。

## 影响范围（Impact）

- 受影响能力：llm-wiki-tools（新增 eval-baseline 需求）。
- 受影响代码：cozo-lib-dotnet-llm-wiki/eval/、tests。
