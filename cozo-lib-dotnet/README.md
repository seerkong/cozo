# Cozo.DotNet

.NET 10 bindings for the [CozoDB](https://www.cozodb.org) embedded database, using the native `cozo_c` shared library via P/Invoke.

## Install

```bash
dotnet add package cozo-lib-dotnet
```

Or add to your `.csproj`:

```xml
<PackageReference Include="cozo-lib-dotnet" Version="0.7.6" />
```

## Quick Start

```csharp
using Cozo.DotNet;

using var db = new CozoDb(engine: "mem", path: "");

// Create a relation
db.Run("?[] <- [[1, 'Alice'], [2, 'Bob']]", immutable: false);

// Query
using var result = db.Run("?[id, name] <- [[1, 'Alice'], [2, 'Bob']]");
Console.WriteLine(result.RootElement);
```

## Ontology Mapping Quick Start

The package also includes a C# OM facade under `Cozo.DotNet.Om`. The core ontology capsule lives in
`src/Om.Core`; the public namespace remains `Cozo.DotNet.Om` for source compatibility. It uses a DEPA-style
capsule: contracts and runtime data stay separate from logic, and only `Support/CozoDbOmStore` calls `CozoDb`
directly.

```csharp
using System.Text.Json;
using Cozo.DotNet;
using Cozo.DotNet.Om;
using Cozo.DotNet.Om.Contracts.Models;
using Cozo.DotNet.Om.Inputs;

using var db = new CozoDb(engine: "mem", path: "");
var om = new CozoOm(db);

await om.InitSchemaAsync();
await om.DefineTypeAsync("Order", "Sales order");
await om.DefineTypeAsync("Shipment", "Shipment");
await om.DefineAttributeAsync("Order", "number", OmValueType.String, required: true);
await om.DefineRelationAsync("has_shipment", "Order", "Shipment");

await om.CreateEntityAsync("o1", "Order", "Order 1");
await om.SetPropertyAsync("o1", "number", "SO-001");

await om.DefineExistentialRuleAsync(
    "order_has_shipment",
    new ExistentialRuleSpec(
        new ExistentialForEachSpec("Order"),
        new ExistentialExistsSpec("has_shipment", ExistentialDirection.Out, "Shipment"),
        new ExistentialMaterializeSpec("shipment:{fromId}"),
        ExistentialRuleMode.Materialize,
        "orders must have a shipment"));

var violations = await om.CheckExistentialRulesAsync();
var chase = await om.ApplyExistentialRulesAsync(new ApplyExistentialRulesInput(MaxIterations: 3));
```

Reusable batch ingestion lives in the separate `src/Om.Batch` capsule and depends on the core OM facade:

```csharp
using Cozo.DotNet.Om.Batch;

await om.IngestBatchAsync(new OmBatchInput(
    Entities: [new OmBatchEntity("o2", "Order", "Order 2")],
    Properties: [new OmBatchProperty("o2", "number", "SO-002")]));
```

Reusable graph/tree/ranking templates live in the separate `src/Om.Analytics` capsule:

```csharp
using Cozo.DotNet.Om.Analytics;

var impact = await om.ImpactAnalysisAsync(new ImpactAnalysisInput(
    RootId: "o2",
    RelNames: ["has_shipment"],
    MaxDepth: 2));
```

Existential rules intentionally support the v1 head shape `exists { rel, direction?, toType }`.
Attribute existence remains modeled as required attributes plus `ValidateEntityAsync`.
Custom validators and computed values are in-memory delegates registered on the facade; their metadata can be persisted, but delegates are not recovered from Cozo facts.

## Building the Native Library

The package requires the `cozo_c` native library. Use the included build script:

```bash
# Build for current platform (auto-detects OS and arch)
./build-native.sh

# Build with explicit RID override
./build-native.sh osx-arm64
./build-native.sh linux-x64
```

This runs `cargo build --release -p cozo_c -F compact -F storage-rocksdb` and copies the resulting library into `runtimes/<rid>/native/`.

### Prerequisites

- [Rust toolchain](https://rustup.rs/) (for building `cozo_c`)
- [.NET 10 SDK](https://dotnet.microsoft.com/download)

### Supported Platforms

| RID           | Library File       |
|---------------|--------------------|
| osx-arm64     | libcozo_c.dylib    |
| osx-x64       | libcozo_c.dylib    |
| linux-x64     | libcozo_c.so       |
| linux-arm64   | libcozo_c.so       |
| win-x64       | cozo_c.dll         |

## Running the Example

```bash
# Run from the cozo-lib-dotnet directory
MSBUILDTERMINALLOGGER=off dotnet run --project example/CozoExample.csproj
```

If your SDK still shows repeated timing lines (for example, `(0.1 秒)`), use this two-step form:

```bash
dotnet build -tl:off example/CozoExample.csproj
dotnet run --no-build --project example/CozoExample.csproj
```

Do not run `dotnet run` directly on `Cozo.DotNet.csproj`: it is a library project, not an executable.

## API

- `CozoDb(engine, path, options?)` — Open a database
- `Run(script, parameters?, immutable?)` — Run a CozoScript query, returns `JsonDocument`
- `RunRaw(...)` — Same but returns raw JSON string
- `ImportRelations(data)` / `ExportRelations(request)`
- `Backup(path)` / `Restore(path)`
- `Dispose()` / `Close()` — Close the database

## License

MPL-2.0
