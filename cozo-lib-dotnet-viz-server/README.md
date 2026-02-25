# Cozo.DotNet Viz Server

ASP.NET Core backend compatible with the `cozo-lib-bun-viz/frontend` API contract.

Run locally:

```bash
dotnet run --project cozo-lib-dotnet-viz-server/Cozo.DotNet.VizServer.csproj
```

The server references `cozo-lib-dotnet` directly and does not require Node or Bun at runtime.

Use a fixed local URL when connecting the existing Vue frontend:

```bash
dotnet run --project cozo-lib-dotnet-viz-server/Cozo.DotNet.VizServer.csproj --urls http://127.0.0.1:5099
cd cozo-lib-bun-viz/frontend
VITE_API_BASE=http://127.0.0.1:5099 vite
```

## API Scope

The server exposes the backend endpoints used by `cozo-lib-bun-viz/frontend`:

- `GET /health`
- `GET /api/demos`
- `POST /api/run`
- `GET /api/permission/models`
- `POST /api/permission/run`
- `GET /api/schema/state`
- `GET /api/schema/versions`
- `POST /api/schema/diff`
- `POST /api/schema/apply`
- `POST /api/schema/rollback`
- `GET /api/governance/seed-template`
- `POST /api/governance/seed`
- `POST /api/governance/checkAccess`
- `POST /api/governance/explain`
- `POST /api/governance/integrity/seed-demo`
- `GET /api/governance/integrity/rules`
- `POST /api/governance/integrity/check`
- `POST /api/governance/integrity/apply`

## Boundary

Reusable ontology capabilities live in `cozo-lib-dotnet`:

- `src/Om.Core` keeps the core ontology facade and DEPA runtime/store boundary.
- `src/Om.Batch` owns typed batch ingestion.
- `src/Om.Analytics` owns graph/tree/ranking template queries.

This package owns HTTP routing, demo catalog data, frontend response adapters, server-scoped demo database lifetimes, and raw CozoScript permission demos.

## Verification

Start the server, then run the contract tests:

```bash
dotnet run --project cozo-lib-dotnet-viz-server/Cozo.DotNet.VizServer.csproj --urls http://127.0.0.1:5099
VIZ_SERVER_BASE_URL=http://127.0.0.1:5099 dotnet run --project cozo-lib-dotnet-viz-server/tests/Cozo.DotNet.VizServer.Tests.csproj
```
