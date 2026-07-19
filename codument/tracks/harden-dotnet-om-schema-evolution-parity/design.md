# Design

## Contract

V2 operations accept explicit options and default to strict handling. They
first normalize and validate all requested operations into a plan. A single
store transaction then applies the plan, writes the snapshot/version/state and
records migration history; any failure leaves the previous observable state.

## Compatibility

Existing `ApplySchemaMigrationAsync` and `RollbackSchemaAsync` remain as
obsolete wrappers retaining their legacy defaults. The V2 facade is idiomatic
C# rather than a literal Bun API.

## Legacy Upgrade

Initialization reports detected legacy shapes only. An explicit V2 upgrade API
or an explicit opt-in performs an idempotent conversion with preservation
tests.

## Diff

The new structured diff keys changes by schema definition/table identity and
includes C#-specific behavior metadata instead of dropping it.
