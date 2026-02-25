# 方案设计：add-cozo-wiki-serve-api

## 上下文

HTTP API 是 viz 前端与 repo DB 的交互边界。Server 只做 presentation/transport，不复制业务逻辑。

## 方案概览

1. `Cozo.DotNet.LlmWiki.Server` 使用 `Microsoft.NET.Sdk.Web`。
2. 暴露 `LlmWikiWebServer.BuildApp(options)` 供测试和 hosting 使用。
3. 每个 server 实例创建一个 `CozoDb` + `CozoOm` + `LlmWikiToolRunner`，生命周期跟随 app。
4. Typed endpoints 内部组装 `JsonObject` 后调用 shared runner。
5. `--static-dir` 存在则 serve static files；缺失时 API-only 启动并 warning。

## API

- `GET /health`
- `GET /api/status`
- `GET /api/tools`
- `POST /api/tools/call`
- `POST /api/index`
- `POST /api/embeddings/index`
- `POST /api/search/semantic`
- `POST /api/symbol/context`
- `POST /api/symbol/impact`
- `POST /api/wiki/build`
- `POST /api/query/named`

## 风险 / 权衡

- ASP.NET Core 引入 web sdk；隔离在 server 包。
- Long-running index operations are synchronous HTTP calls in MVP; frontend can show loading state.
- Static frontend path may vary; provide explicit `--static-dir` override.
