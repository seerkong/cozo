# 变更：skills 自动生成

## 背景和动机 (Context And Why)

mission `harden-llm-wiki-engineering`（P3）G3。GitNexus 用 `--skills` 按社群生成 `.claude/skills/generated/<community>/SKILL.md` + 4 个通用工作流 skill，让 agent 在会话中按需加载仓库结构知识。cozo-wiki 已有更好的输入：确定性 context 发现（G4/P2 modeling fractal 的三级兜底命名）、调用图、执行流、18 工具面。本 track 做 `cozo-wiki skills generate`。

## "要做"和"不做" (Goals / Non-Goals)

**目标:**
- per-context skill：复用 modeling context 发现 → `.claude/skills/cozo-wiki-generated/<context-slug>/SKILL.md`——frontmatter（name/description 供技能触发）+ 结构层确定性内容：context 职责/边界、关键符号（度数 top，file:line）、内部调用关系概览、参与执行流、"探索该区域的 cozo-wiki 工具调用示例"（semantic_search/symbol_context/trace 的现成参数）。有界（MaxSkills 默认 24 复用 context 上限；每 skill 字符预算 4000）。
- 通用工作流 skill（静态模板 3 个）：cozo-wiki-exploring（如何用 18 工具导航）、cozo-wiki-impact（改动影响工作流：detect_changes→impact→trace）、cozo-wiki-depa（健康度工作流：depa_conformance→fact_grade_map）。
- 生成纪律：目标目录只写自有子目录（cozo-wiki-generated/ 与 cozo-wiki-* 前缀），`skills clean` 只删自有；重复 generate 幂等覆盖；显式动作不默认。
- dogfood：本仓副本生成，抽样评注 skill 可用性。

**非目标:**
- 不做 LLM 叙述（纯结构层——skills 是导航物，确定性优先；后续可加）。
- 不写本仓 .claude/skills/（dogfood 在副本）。

## 变更内容（What Changes）

- McpServer：`skills generate|clean|status` 子命令 + internal SkillsGenerator（复用 Wiki 包 context 发现——评估复用点，若 context 发现 internal 在 Wiki 包则加 IVT 或走 Tools 层查询 ck_community/ck_member 自组，取依赖最干净者记 findings）。
- tests。

## 影响范围（Impact）

- 受影响能力：llm-wiki-tools（新增 skills-generation 需求）。
- 受影响代码：packages/McpServer（或 Wiki）、tests。
