# Design

## Verification Model

The track treats the three completed implementation tracks as evidence inputs,
not as proof by declaration. It constructs one matrix per G15-G17 with the
desired contract, persisted/runtime boundary, Bun evidence, .NET evidence,
executed command, verdict, and residual risk.

## Contract Boundaries

- **G15:** a runtime instance owns callback registration. Clear/reset changes
  runtime callback facts only and must not delete persisted definitions or
  binding identities.
- **G16:** canonical V1 JSON records durable definition and binding metadata.
  Readiness is a projection of the current runtime registry; unresolved
  execution fails closed and executable source never enters the wire format.
- **G17:** all behavior definitions and `om_behavior_binding` participate in
  Bun snapshot, keyed diff, migration, and atomic rollback. Restores never
  restore executable readiness. A missing legacy behavior section rejects by
  default with `OMSV1001` and zero effects; explicit `preserve` or `clear` is
  required.

## Execution

1. Build the evidence matrix from code, tests, frozen D6-D8, and reports.
2. Run paired focused scenarios and both runtime regression gates; validate
   linked tracks and the new track in strict mode.
3. Publish an independent issues-first verdict, then run final attractor and
   GapLoop checks before the mission completed gate.

## Repair Boundary

A discovered gap may be fixed only when it is a decision-free defect in the
already frozen contract. A semantic choice, a conflict with D6-D8, or a
materially different design is a blocking finding for the user rather than a
silent rewrite.
