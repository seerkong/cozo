# Decisions

## Usage

- 本文件记录 mission 期间需要保持的技术/产品决策。
- durable 决策后续可拆入 `decisions/` 并在 archive-mission 时提升。

### 1. 【P0】首批能力边界

- 背景：GitNexus 功能很宽，但当前目标是基于 Cozo + C# OM 的可运行 MVP。
- 最终决策：首批只实现 scanner/indexer/wiki/MCP stdio，不实现 Tree-sitter/PDG/embedding/Web UI。
- 决策理由：当前 `Om.CodeKnowledge` 已能承载 facts 和 agent facade，先补产品化外壳最有收益。
- 状态：accepted

### 2. 【P0】代码落点

- 背景：用户指定在 `cozo-lib-dotnet-llm-wiki/packages` 下创建多个 dotnet project。
- 最终决策：创建独立 `Cozo.DotNet.LlmWiki.*` projects，通过 ProjectReference 依赖现有 `cozo-lib-dotnet/Cozo.DotNet.csproj`。
- 决策理由：保持应用层包与 Cozo OM 核心解耦，便于未来独立发布。
- 状态：accepted
