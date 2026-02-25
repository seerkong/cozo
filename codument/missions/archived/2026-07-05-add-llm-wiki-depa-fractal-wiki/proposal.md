# Mission：add-llm-wiki-depa-fractal-wiki（P2 · 分形 Wiki 差异化 + DEPA 健康度）

## 背景和动机

这是 cozo-wiki 相对 GitNexus 的**差异化愿景层**（2026-07-04 gap 分析）。

**GitNexus wiki 现状**（借鉴其骨架、不抄其目标结构）：LLM 三阶段管线（`gitnexus/src/core/wiki/generator.ts`）——Phase1 LLM 把文件分组成模块树 → Phase2 per-module LLM 生成页面（输入是模块内/间调用边 + 执行流，输出含 mermaid）→ Phase3 LLM 总览页；git-diff 增量（changed files → 所属模块 → 只重生成受影响页，.meta.json 缓存 moduleTree）；多 LLM 后端与多输出语言。目标结构是扁平的 wiki/modules/*.md。

**本 mission 的两个更大目标**：

### 目标 A：DEPA 健康度识别（理论源：~/.claude/skills/depa-expert/）

用代码图识别 DEPA 概念并评估 `output = fn(runtime, input, config)` 的符合程度：

- **effect 泄漏检测**：CALLS 边 + IO/副作用 API 标注（BCL/框架白名单起步）→ 核心逻辑是否绕过 runtime 契约直接产生副作用。
- **runtime/input/config 归位**：函数签名 + 参数类型 + ACCESSES 使用方式 → 对照 depa-expert 的 runtime-explicitness 决策表（runtime=长生命周期依赖/契约；input=单次调用不可变载荷;config=静态策略，禁函数对象）。
- **事实源 7 级分级**（fact-grade-classification）：写引用图 → authoritative_fact / domain_canonical_event / … / surface_view 分级；检测红灯：多写入者、投影反写、多源冲突。
- **capsule 符合度**：目录结构 + 导出面 + 跨模块引用 → 单一入口/internals 隔离判定。
- 判定纪律沿用 depa-expert：每个违反信号必带 path:line 证据；观测优先于猜测；PASS/GAP/BLOCKED。

DEPA 概念天然落在 OM 本体层（GitNexus 没有的资产）：capsule/port/contract/mailbox/reducer/projection/fact-grade 建模为 OM Type，违反信号用 existential rule / constraint 表达。派生新工具：`depa_conformance`、`fact_grade_map`、`health_score`。

### 目标 B：分形文档结构自动生成（规范源：/Users/kongweixian/ai/ai-codument/codument/src/templates/codument/std/）

wiki 生成目标 schema 换成两套分形（docs-modeling-fractal / docs-engineering-fractal / folder-manifest.md / knowledge-tiers.md / model-driven-docs.md，本仓 codument/std/ 下也有同步副本）：

- **engineering 分形**（先做，LLM 依赖最少）：`docs/impl/<plane>/{overview,howto,rules,reference,troubleshooting}/`——overview 从社群 + 模块间边生成；reference/code-map 从图直接导出。
- **modeling 分形**：`docs/modeling/<plane>/contexts/<context>/{objects,policies,workflows}/`——context 划分 ≈ 社群检测 + 业务边界；objects 从类型定义/字段/生命周期反推；workflows 从执行流反推；derived plane 填 derived_from 单一父来源。
- 每个目录生成 **folder-manifest 职责块**（holds/excludes/tier/⬆from/⬇to）；frontmatter 受控字段（doc_role/status/last_verified/context/derived_from），禁堆字段。
- 递归规则不变量：plane → context/类目 → 叶子；index 只导航；单文件 → index.md → 子文件的演化路径。

## 目标

1. LLM 驱动 wiki 管线（借 GitNexus 骨架：图查询喂 LLM + git-diff 增量 + 多后端），替换纯模板 WikiCompiler。
2. engineering 分形生成（overview/reference/code-map 优先）。
3. modeling 分形生成（context/objects/workflows）。
4. DEPA 概念 OM 本体建模 + effect API 标注机制。
5. `depa_conformance` / `fact_grade_map` / `health_score` 工具第一版。

## 非目标

- 不做 wiki 交互式站点（Markdown 真源优先）。
- 不做全自动文档晋升（knowledge-tiers 晋升只给建议，不自动改 owner 文档）。
- DEPA 第一版不覆盖 Actor 维 mailbox 动态语义（静态图可判定的优先）。

## 成功判据

- 对 cozo-lib-dotnet* 自身生成的分形文档结构通过 folder-manifest/model-driven-docs 规范检查（职责块齐全、frontmatter 受控、derived_from 单父）。
- DEPA 工具对本仓 Om.Core（已知 DEPA 合规样本）和一个故意违规样本给出可区分、带 path:line 证据的结论。
- wiki 增量：改动单文件后只重生成受影响页。

## 依赖与为什么是 mission

硬依赖 P0（deepen-llm-wiki-code-graph：调用图/社群/执行流），部分依赖 P1（工具面模式）。两条愿景线（DEPA / 分形文档）+ LLM 管线基建，跨多 track、含理论到工程的转化决策，必须 mission 编排。
