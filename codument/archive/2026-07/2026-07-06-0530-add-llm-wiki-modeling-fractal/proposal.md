# 变更：modeling 分形生成（context/objects/workflows）

## 背景和动机 (Context And Why)

mission `add-llm-wiki-depa-fractal-wiki`（P2）G4。engineering 分形（G3 已归档）覆盖 docs/impl 侧；本 track 补 docs/modeling 侧——ai-codument 双分形架构的另一半：`modeling/<plane>/contexts/<context>/{objects,workflows}/`（canonical 领域真源形态）。context 划分消费 P0 社群（G3 交接的 `#N` 退化命名在此必须解决：社群 label + 命名空间/目录聚合 + LLM 归组审查稿兜底）。G3 的 FractalSpecChecker 已把 M-* 20 条规则显式列为 NotImplementedRuleIds 留给本 track。

## "要做"和"不做" (Goals / Non-Goals)

**目标:**
- context 发现：社群 + 命名空间前缀聚合 → context 候选（确定性）；LLM 归组审查稿（review 稿模式，沿用 G2 管线的 grouping-review 机制）可改名/合并；`#N` 退化名在 modeling 侧禁止（无公共前缀时用最高度数类型名 + 目录段兜底）。
- 骨架：`modeling/index.md` + `modeling/domain/index.md` + `contexts/<ctx>/index.md` + `objects/`、`workflows/` 类目（policies 不建——空类目规则，代码仓无 policy 证据；记决策）。
- 叶子：objects/<type>.md（从 ck_symbol 类型+成员+继承边确定性渲染，含 code-map 链接与 LLM 叙述槽）；workflows/<process>.md（从 ck_process 步骤链渲染 + 叙述槽）；每 context 的 code-map.md。
- frontmatter：context 字段（modeling 侧）、doc_role=canonical；职责块齐全。
- FractalSpecChecker 补齐 M-* 20 条 [P] 规则 + discrimination mutations；dogfood 本仓 [P] 100%。
- 与 engineering 分形共存：同一 OutputDirectory 下 modeling/ 与 impl/ 并列，migration-map 与 .meta 增量体系复用。

**非目标:**
- 不做 derived plane（backend/surface 等，只出 domain canonical plane）。
- 不做 policies 类目（无证据源）。
- 不做 docs/ 真源同步（仍 preview 边界）。

## 变更内容（What Changes）

- Wiki 包：FractalWikiPipeline 扩展 modeling 平面生成（页面注册表 + 哈希增量并入既有体系）；FractalSpecChecker 补 M-* 规则。
- tests：modeling 结构/叶子/checker mutations/dogfood。

## 影响范围（Impact）

- 受影响能力：llm-wiki-pipeline（新增 modeling-fractal 需求）。
- 受影响代码：packages/Cozo.DotNet.LlmWiki.Wiki、tests。
