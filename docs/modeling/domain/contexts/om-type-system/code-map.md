---
knowledge_plane: domain
doc_role: reference
status: active
context: om-type-system
last_verified: 2026-07-01
---

# OM Type System Code Map

| Area | Source |
|------|--------|
| Bun reference implementation | `cozo-lib-bun/cozo-om.js` |
| Bun parity tests | `cozo-lib-bun/__tests__/om-type-hierarchy.test.js`, `om-attr-inheritance.test.js`, `om-mixin.test.js`, `om-alias-resolution.test.js`, `om-validity-attr.test.js`, `om-polymorphic-query.test.js`, `om-rel-inheritance.test.js`, `om-description.test.js`, `om-backward-compat.test.js` |
| .NET type logic | `cozo-lib-dotnet/src/Om.Core/Logic/TypeLogic.cs` |
| .NET entity/query logic | `cozo-lib-dotnet/src/Om.Core/Logic/EntityLogic.cs` |
| .NET relation validation | `cozo-lib-dotnet/src/Om.Core/Logic/RelationLogic.cs` |
| .NET validity conversion | `cozo-lib-dotnet/src/Om.Core/Internals/OmConvert.cs` |
| .NET model contracts | `cozo-lib-dotnet/src/Om.Core/Contracts/Models/OmModels.cs` |
| .NET parity tests | `cozo-lib-dotnet/tests/Program.cs` |

