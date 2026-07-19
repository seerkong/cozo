# Design: Domain-neutral ontology surfaces

## Boundary

```text
domain fixtures/examples ── explicit and replaceable ──> tests/demo packages
                                                        |
generic library contracts <── structural facts/evidence ┘
```

Library code may reason about source structure: typed field versus method reference, direct evidence, canonical concept mappings, guarded state changes, cross-file paths and candidate conflicts. It must not reason about whether a project has asset intake, borrowing, transfer, maintenance, disposal, asset numbers, or equivalent customer vocabulary.

## Production replacement

`BusinessOntologyQualityReportBuilder` will replace required asset workflow category coverage with generic evidence diversity diagnostics: semantic candidates must be backed by direct structural/guard/state evidence and, when a use-case slice exists, cross-file evidence. A report may emit a coverage warning based on observed entry-point families, but no fixed business category is mandatory.

`BusinessOntologySemanticProjector` will retain its generic operation-like relation-name filter and drop the `ByAssetNumber` exceptions. Structural field/record-component references remain the positive relation signal.

## Demo and fixtures

The visualization hierarchy demo becomes a neutral `resource-graph` demo using `Resource`, `ComputeNode`, `Workspace` and `Service` objects. Its assertions continue to demonstrate inheritance, mixins, properties, edges, polymorphic query, impact analysis and governance.

Tests and skills use `Record`, `Party`, `Workspace`, `WorkflowItem` and neutral state names rather than IT-asset terms. Renames are mechanical only where the same semantic contract remains; fixture behavior is preserved by tests.

## Verification

- targeted .NET LlmWiki test harness;
- Bun unit and visualization E2E/API tests affected by the demo rename;
- skill script tests;
- scoped source scan excluding `codument/archive/**` for forbidden asset-domain terms.
