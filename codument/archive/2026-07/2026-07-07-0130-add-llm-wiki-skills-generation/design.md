# 方案设计：skills 自动生成

## 上下文

输入：ck_community/ck_member（G5-T1 社群）、modeling context 发现逻辑（Wiki 包 internal BuildModelingContexts——三级兜底命名禁 #N）、ck_symbol/ck_edge/ck_process*、18 工具面。GitNexus 对标：generated skills 按社群 + 4 通用 skill。Claude Code skill 形态：`.claude/skills/<name>/SKILL.md`，frontmatter name/description 供触发。

## 方案概览

1. **context 复用**：优先复用 Wiki 包 BuildModelingContexts（IVT McpServer→Wiki 或把 SkillsGenerator 放 Wiki 包由 McpServer 接线——取依赖最干净者，倾向 SkillsGenerator 落 Wiki 包（已有 context/图快照基础设施），CLI 只接线；记 findings。
2. **per-context SKILL.md**（确定性渲染，字符预算 4000/skill）
   - frontmatter：name=cozo-wiki-<context-slug>、description="Navigate the <context> area of this repo (key symbols, call structure, processes). Use when working on files under <主路径前缀>."
   - 正文：Boundary（成员文件前缀/符号数）、Key symbols（度数 top8，`name — kind (path:line)`）、Call structure（跨 context 依赖 top3）、Processes（top3 名称+入口）、Explore with cozo-wiki（3 条现成工具调用示例：semantic_search/symbol_context/trace 带真实参数）。
3. **通用 workflow skills**（静态模板 + 少量图数字插值）：cozo-wiki-exploring / cozo-wiki-impact / cozo-wiki-depa。
4. **CLI**：`skills generate [--work-dir] [--target-dir 默认 <work-dir>/.claude/skills] [--max-skills]`、`skills clean`、`skills status`；自有辨识 = 目录名前缀 `cozo-wiki-`；generate 幂等覆盖自有、零触碰其他；目标内自有但 context 已消失的目录在 generate 时清理（同步语义）。
5. **dogfood**：/tmp 副本仓生成，抽样评注。

## 影响范围与修改点（Impact）

- Wiki 包：internal SkillsGenerator（+渲染模板）；McpServer：skills 子命令接线；tests。

## 决策摘要

- 零 LLM（导航物确定性优先）；自有前缀 `cozo-wiki-`；同步语义（消失 context 的 skill 目录随 generate 清理）。

## 风险 / 权衡

- skill 触发质量取决于 description 措辞 → 模板含路径前缀与用途动词；后续可迭代。
- 大仓 context 多 → MaxSkills 复用 context 上限 24。

## 最小公开面

零新增 public 类型（SkillsGenerator internal + IVT）。
