# 方案设计：verify-llm-wiki-viz-e2e

## 方案概览

1. README 增加 Server 与 Viz Frontend sections。
2. 复跑所有关键构建和 smoke。
3. 若验证通过，标记 track 与 mission 完成。

## 验证命令

- `dotnet build cozo-lib-dotnet-llm-wiki/Cozo.DotNet.LlmWiki.slnx`
- `dotnet run --project cozo-lib-dotnet-llm-wiki/tests/Cozo.DotNet.LlmWiki.Tests/Cozo.DotNet.LlmWiki.Tests.csproj`
- `npm run build` in `cozo-lib-dotnet-llm-wiki-viz`
- `cozo-wiki --serve --static-dir cozo-lib-dotnet-llm-wiki-viz/dist` + `GET /`
