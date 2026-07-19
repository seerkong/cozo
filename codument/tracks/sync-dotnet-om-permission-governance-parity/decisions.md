# Decisions

## Inherited Decisions

- D0=A: parity is observable semantics with idiomatic typed C# APIs.
- D1=A: use strict graph witnesses and complete temporal/entity-aware ABAC; do not retain a permissive legacy mode.

## Execution Rules

- A declared path is an ordered relation-name sequence. It grants no access unless it has a concrete directed witness from subject to resource at the requested temporal point.
- Empty path only witnesses subject equal to resource; malformed/unknown paths fail closed with structured explanation detail.
- ABAC references are limited to `subject.type`, `resource.type`, `subject.<attribute>`, `resource.<attribute>`, and explicit literals. Unsupported references/operators fail closed.
- `field.<name> hide <truthy-expression>` contributes `hidden` visibility only after the policy's witness and non-hide ABAC predicates match.
