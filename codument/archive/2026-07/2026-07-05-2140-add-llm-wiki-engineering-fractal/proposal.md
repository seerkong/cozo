# 变更：engineering 分形文档生成器

## 背景和动机 (Context And Why)

mission `add-llm-wiki-depa-fractal-wiki`（P2）G3。G2 管线基建已就位（四阶段、docs-preview、增量、review 闸门），先导页只有 overview 骨架与 code-map。本 track 把产出升级为完整 engineering 分形（docs-engineering-fractal 规范）：plane→类目→主题递归、六类目、目录职责块（folder-manifest）、受控 frontmatter（model-driven-docs）。验收依据是 mission G1-T1 定稿的机器可断言 checklist（analysis/fixtures.md 的 E-A..E-H 30 条）。

## "要做"和"不做" (Goals / Non-Goals)

**目标:**
- 根结构：docs-preview/{index.md, migration-map.md, impl/}（modeling/ 目录 G4 建）。
- impl plane 生成：global plane（整仓）+ 按分组的 plane 归属（MVP：单 global plane，组作为 overview/reference 的小节与 per-group 主题页；多 plane 演化留后续）。
- 六类目落地：overview（骨架+叙述段）、reference（code-map + api 表，零/低 LLM）、howto/rules/troubleshooting（骨架页：从图可确定的候选条目 + llm 叙述段/pending）、examples（占位 index 说明何时填充——记录取舍）。
- 目录职责块：每类目 index.md 顶部精简型职责块（holds/excludes/tier/⬆from/⬇to）；impl plane index 用完整型。
- frontmatter 受控字段：knowledge_plane/doc_role/status/last_verified（值域按规范；禁数组堆砌）。
- 内置 FractalSpecChecker（internal）：对产出跑 fixtures [P] 条目断言（测试与后续工具共用）。
- dogfood：本仓生成 + [P] 100% 通过 + [L] 抽样评注记 findings。

**非目标:**
- modeling 分形（G4）。
- docs/ 真源同步工具（preview 为界）。
- 多 plane 自动划分（单 global plane MVP，记录演化路径）。

## 变更内容（What Changes）

- Wiki 包：FractalWikiPipeline 扩展 engineering 全形页面生成 + FractalSpecChecker。
- tests：checklist 断言 + dogfood。

## 影响范围（Impact）

- 受影响能力：llm-wiki-pipeline（新增 engineering-fractal 需求）。
- 受影响代码：packages/Wiki、tests。
