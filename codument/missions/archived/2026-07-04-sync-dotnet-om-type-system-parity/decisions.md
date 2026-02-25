# Mission Decisions

## 2026-07-01

- Create mission `sync-dotnet-om-type-system-parity` in `pending` to coordinate multiple tracks that synchronize `cozo-lib-dotnet` OM type-system kernel semantics with `cozo-lib-bun`.
- Treat the archived Bun type hierarchy spec and current Bun tests as the reference behavior for mixin attributes, inherited attribute restrictions, alias compatibility, `Validity` values, and polymorphic type queries.
- Preserve .NET OM DEPA capsule boundaries: behavior remains in `Om.Core/Logic`, Cozo effects remain behind `ICozoOmStore`, and public facade additions delegate to logic functions.
- Treat relation `directed=false` behavior as a reconciliation decision because .NET currently implements reverse endpoint acceptance while Bun currently does not use the stored flag during validation.
