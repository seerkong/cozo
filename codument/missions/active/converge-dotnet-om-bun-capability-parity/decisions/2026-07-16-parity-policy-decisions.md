# Human Decision Record: 2026-07-16

## Source

The user selected the following options before mission implementation:

- D0 parity standard: A.
- D1 permission model: A.
- D2 schema migration and rollback: B.
- D3 manifest and script runtime: C.
- D4 preservation of mature C# design: approve all.
- D5 explicit parent clearing: C.
- D3a manifest format: A.
- D3b unresolved callback import: A.

## Applied Interpretation

- D0 A means observable behavioral parity with idiomatic C# API shapes.
- D1 A means a hard cut to strict Bun-compatible path witness and complete ABAC semantics, with no legacy evaluation mode.
- D2 B means additive atomic/strict V2 APIs, obsolete compatibility wrappers, detect-only legacy initialization by default, explicit migration, and optional automatic upgrade.
- D3 C resolves the runtime to in-process Jint. D4 requires the Jint dependency to remain outside `Om.Core` behind an explicit provider boundary.
- D4 preserves all five evidence-backed C# mechanisms listed in the mission decision summary.
- D5 C introduces `Keep | Set | Clear` patch/options semantics and migrates away from the ambiguous overload.
- D3a A makes JSON canonical and treats YAML as a normalization adapter.
- D3b A allows inactive/unresolved metadata import, requires fail-closed execution, and adds an atomic `requireReady` mode.

## Closure

D3a and D3b were answered explicitly. The decision frontier for this mission is empty as of 2026-07-16T17:08:31Z.
