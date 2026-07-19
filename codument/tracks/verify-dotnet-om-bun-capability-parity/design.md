# Design

## Verification Model

The track uses goal-backward verification. Each G01-G14 row is evaluated at
three levels:

1. Exists: the API, rule, package, or governance path is present.
2. Substantive: executable tests exercise success, rejection, rollback, temporal
   behavior, diagnostics, or inheritance as appropriate.
3. Wired: the public facade and real runtime path reach the verified behavior.

## Evidence Sources

- Mission evidence and frozen decisions define the desired contract.
- Linked track reports and current source define the implemented state.
- Fresh Bun and .NET executions define the final runtime state.
- Codument strict validation defines control-plane integrity.

No row passes solely because a report says it passed. Runtime-sensitive rows
must include fresh command evidence, and linked-track states must resolve to
real `track.xml` files.

## Matrix Schema

The final report contains one row per G01-G14 with:

- desired behavior;
- Bun implementation/test evidence;
- .NET implementation/test evidence;
- frozen decision or intentional divergence;
- fresh command evidence;
- verdict `PASS`, `FAIL`, or `BLOCKED`;
- residual risk.

## Failure Handling

- A missing or failing paired test with unambiguous desired behavior is repaired
  inside this track and then reverified.
- A failure that changes security, migration defaults, serialization, scripting,
  or public compatibility reopens the owning decision and returns `BLOCKED`.
- Environment failures are not treated as parity evidence.

## Shared Working Tree Boundary

The workspace contains concurrent unrelated changes. Executors preserve them,
review only mission-owned paths and relevant hunks, and never use destructive
Git cleanup commands.
