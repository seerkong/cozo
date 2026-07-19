# Strict Permission Governance Parity

## Goal

Make `cozo-lib-dotnet` permission decisions observably match Bun's strict permission model: real graph witnesses, entity-aware temporal ABAC, deny precedence, field visibility and explainable fail-closed outcomes.

## Background

Current C# code accepts a matching policy while treating stored path strings as explanation text. That can allow an unrelated subject to read a resource. It also ignores `AsOf` and has a narrower ABAC language. D1=A explicitly rejects preserving that permissive behavior.

## Scope

- Strictly parse and evaluate declared paths against current or as-of relation edges.
- Resolve subject/resource types and attributes at the requested temporal point.
- Support Bun-equivalent equality and ordered comparisons; fail closed for invalid inputs.
- Return typed field visibility and detailed per-policy witness/ABAC explanations.
- Add C# and Bun conformance cases for allowed, denied, temporal, malformed and field-hide paths.

## Non-Goals

- A new policy storage engine, cross-process identity provider, or a compatibility mode that permits explanatory-only paths.
- Copying JavaScript API shape into C# where typed contracts are clearer.

## Success Criteria

- A policy with no concrete witness never grants access.
- A valid witness and all ABAC predicates grant only when no matching deny exists.
- `AsOf` changes both graph and attribute evaluation consistently.
- Field hide and explanation results are deterministic and testable on both implementations.
