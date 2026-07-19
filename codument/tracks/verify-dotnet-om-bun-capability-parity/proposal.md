# Change: Verify .NET OM and Bun Capability Parity

## Context And Why

The convergence mission has completed its implementation slices for behavior
runtime, portability, optional scripting, permission governance, schema
evolution, existential governance, and public APIs. The mission cannot close
from per-track reports alone: it requires an independent cross-runtime audit
that executes both suites and resolves every original G01-G14 evidence item.

## Goals / Non-Goals

Goals:

- Produce one executable and reviewable G01-G14 evidence matrix.
- Run Bun and .NET full suites plus focused parity fixtures.
- Validate all bound implementation tracks and their completion evidence.
- Record intentional C#-idiomatic differences under the frozen user decisions.
- Repair only concrete verification gaps that do not require a new semantic
  decision.

Non-Goals:

- Do not redesign the OM object/type system.
- Do not require identical JavaScript and C# method signatures.
- Do not reopen D0-D5 or D3a/D3b without contradictory runtime evidence.
- Do not modify Wiki, Java/Spring indexing, visualization, analytics, or batch
  capabilities.
- Do not archive the mission or implementation tracks.

## What Changes

- Add a final cross-runtime verification contract.
- Audit existing paired tests and add only missing cases needed to make a G-row
  executable.
- Run both runtime suites and Codument structural validation.
- Publish an archive-ready final evidence report with explicit residual risk.

## Impact

- Behaviors: `capability-parity-verification`.
- Source/test scope: `cozo-lib-bun`, `cozo-lib-dotnet`, and the mission-linked
  Codument tracks.
- Runtime production code changes are expected only if fresh verification proves
  a concrete, decision-free gap.
