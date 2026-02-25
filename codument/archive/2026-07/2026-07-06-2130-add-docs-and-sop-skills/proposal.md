# 变更：沉淀文档与 SOP skill 化（用户手册 + 双分形补齐 + skill 沉淀）

## 背景和动机 (Context And Why)

P0–P3 四个 mission 完成后功能基本齐备，但知识资产滞后于代码：

1. **终端用户无手册**：cozo-wiki 已有 18 个 MCP 工具、hooks、skills 生成、DEPA 报告等使用面，只有两个 README，缺快速开始与工具参考。
2. **分形文档缺位**：docs/ 双分形树仅覆盖 om-type-system 一个 context，本轮交付的代码知识图（ck_*）、llm-wiki 工具面、Om.Depa 全部未入树——违背项目自己定的 docs-{modeling,engineering}-fractal 标准。
3. **执行 SOP 未沉淀**：~28 个 track 的执行中验证出多套可复用流程（orchestrator 派发、GapLoop 探针、codex 双模式 eval、DEPA 整改闭环），只存在于会话记忆与 memory 文件，换人/换会话即失传。

## "要做"和"不做" (Goals / Non-Goals)

**目标:**
- P1：cozo-lib-dotnet-llm-wiki/docs/ 独立用户手册（七部分），README 瘦身为入口；内容与 CLI 实测一致。
- P2：`cozo-wiki wiki` 自举生成骨架到 staging（dogfood），人工按双分形标准充实，与既有 om-type-system 树合并；新增 code-knowledge / llm-wiki / depa 等 context 与对应 impl 知识。
- P3：4 个 SOP skill 沉淀到 .agents/skills/（subagent-orchestration、gap-loop-probe-design、codex-dual-eval、depa-rectification-loop），引用不复制 codument/std/sop/。

**非目标:**
- 不改产品代码（纯文档/知识 track；自举若暴露产品缺陷登记 backlog 不就地修）。
- 不启用 modeling/engineering XNL registry（config 均 disabled，维持 legacy docs/ Markdown 真源）。
- 不重写既有 om-type-system 文档；不动 codument/std/sop/ 既有文件正文（skill 引用之）。
- 手册不覆盖 cozo-lib-dotnet OM 库 API 文档（另属 cozo-lib-dotnet/README 与分形 impl 树的范围）。

## 变更内容（What Changes）

- 新增 cozo-lib-dotnet-llm-wiki/docs/（手册七部分 + index）；改写 cozo-lib-dotnet-llm-wiki/README.md（瘦身）。
- 扩展仓库 docs/modeling/domain/contexts/（新增 3 个左右 context）与 docs/impl/（新增类目/叶子），全部带目录职责块。
- 新增 .agents/skills/{subagent-orchestration,gap-loop-probe-design,codex-dual-eval,depa-rectification-loop}/SKILL.md（gitignored，本机生效）。

## 影响范围（Impact）

- 受影响的能力（behaviors）：llm-wiki-user-docs（新）、cozo-repo-fractal-docs（新）、agent-sop-skills（新）。
- 受影响的代码：无产品代码变更；docs/、cozo-lib-dotnet-llm-wiki/docs/、cozo-lib-dotnet-llm-wiki/README.md、.agents/skills/。
