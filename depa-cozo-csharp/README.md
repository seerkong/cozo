# Depa.Cozo

`Depa.Cozo` is the thin .NET binding for Cozo's native C API. It provides database lifecycle and raw CozoScript/JSON operations only; it deliberately does not include an object model, ontology DSL or business behavior system.

## Use

```csharp
using Depa.Cozo;

using var db = new CozoDb("mem");
var json = db.RunRaw("?[value] <- [[42]]");
```

The NuGet package uses the standard `runtimes/<RID>/native/` layout. Restore and publish select the native library for the application's RID. The shipped targets are the existing macOS arm64 slot and Windows x64 (`runtimes/win-x64/native/cozo_c.dll`). `osx-x64` and `linux-x64` are not included.

## Source build

Run `bash build-native.sh` from this directory to build and stage the host native library. Then run:

```bash
dotnet pack Depa.Cozo.csproj
```

The release workflow collects native artifacts from each supported host before packing, so a public package contains every supported runtime asset.
