---
name: model-business-knowledge-cozo-om
description: Generate and verify a runnable Bun or .NET Cozo OM model from validated BO-first ontology XML. Use when turning BusinessObject, Association, portable policy/lifecycle behaviour, and explicit RuntimeBinding declarations into code; do not hand-model from raw claims or a ledger alone.
---

# Model BO-First Knowledge with Cozo OM

Require validated canonical XML, not raw observations or an unreviewed ledger. Read `../ontology-xml-dsl/SKILL.md` first.

1. Validate BO ownership, FQN references, direct evidence material, portable expressions, and selected target bindings.
2. Generate the schema in dependency order: BO types/properties, associations, portable rules/lifecycles, then registered extension points.
3. For Bun, create an ESM package with `cozo-lib-bun` declared as a local `file:` dependency, an idempotent installer, Chinese business comments, and real in-memory `CozoDb` tests.
4. For .NET, generate the corresponding OM installer and tests only when all selected ExtensionPoints have `DotNet` bindings.
5. Fail generation for unsupported portable vocabulary or missing bindings. Do not emit a no-op callback, copy host-language code into XML, or hand-edit generated source.

For an ExtensionPoint, preserve `InternalLogic` only as target-side documentation/comments. Never parse it as a branch grammar, predicate, or executable behaviour.

Keep code grouping BO-first where practical: one installer region/module per `BusinessObject`, then separate association/process/policy registration. The package may contain target-side implementation code only behind XML-declared `RuntimeBinding` IDs.
