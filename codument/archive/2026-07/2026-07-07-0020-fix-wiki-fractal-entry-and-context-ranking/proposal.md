# 变更：wiki 分形一级入口（codument-fractal）+ modeling context 重要性排序（W-1/W-2）

## 背景和动机 (Context And Why)

track add-docs-and-sop-skills P2 自举 dogfood 登记的 7 条候选中最重要的两条（backlog W-1/W-2），用户指示立即修复，并指定管线命名为 **codument-fractal**：

1. **W-1**：`cozo-wiki wiki` 子命令只走 legacy WikiCompiler，分形管线只能 `call build_wiki --pipeline fractal` 绕行——分形是主打能力却无 CLI 一级入口。
2. **W-2**：`FractalWikiPipeline.BuildModelingContexts` 的 MaxModelingContexts=24 预算按"成员数降序"截断（FractalWikiPipeline.cs:469-480），无重要性排序——本仓 230 个 context 被丢 206 个，Om.CodeKnowledge、Om.Depa.DepaScanPipeline、LlmWiki.Tools 等核心 namespace 被丢，JS demo 函数级微社群（成员多但全非 public API）反而入选。

## "要做"和"不做" (Goals / Non-Goals)

**目标:**
- `cozo-wiki wiki` 增加 `--pipeline` 选项，取值 `legacy`（默认，行为不变）| `codument-fractal`（走 FractalWikiPipeline）；`build_wiki` 工具的 pipeline 参数同步以 `codument-fractal` 为规范名，`fractal` 保留为静默兼容别名（既有脚本/测试不破坏）。
- context 预算截断改为重要性排序：public-API 成员数（降）→ 总成员数（降）→ 名称（升）；丢弃 diagnostics 保留并注明排序依据。
- 本仓复扫对比：Om.CodeKnowledge / Om.Depa 侧核心 namespace 入选、纯 demo 微社群出局（数字为准，如实记录）。

**非目标:**
- 不做 misc 归并容器与微社群折叠（W-2 备选方案之一、W-3 粒度混杂——另行）；不动 W-4..W-7。
- 不改 FractalWikiPipeline 四阶段结构、增量缓存键与 checker。
- 不改默认 MaxModelingContexts=24 数值。

## 变更内容（What Changes）

- McpServer/Program.cs：wiki 子命令 `--pipeline` 分发；Tools/LlmWikiToolRunner.cs：pipeline 规范名 codument-fractal + fractal 别名 + schema 描述更新。
- Wiki/FractalWikiPipeline.cs：BuildModelingContexts 预算排序改造。
- 两侧 tests；用户手册 cli-reference.md / skills-and-wiki.md / mcp-tools.md 与 docs/modeling llm-wiki context 同步；backlog W-1/W-2 划掉。

## 影响范围（Impact）

- 受影响的能力（behaviors）：llm-wiki-pipeline（wiki 入口）、dotnet-llm-wiki（build_wiki 工具面/context 预算语义）。
- 受影响的代码：packages/Cozo.DotNet.LlmWiki.{McpServer,Tools,Wiki}/、tests、cozo-lib-dotnet-llm-wiki/docs/、docs/modeling/domain/contexts/llm-wiki/。
