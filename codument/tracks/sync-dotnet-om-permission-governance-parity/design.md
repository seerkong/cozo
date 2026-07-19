# Design

## Evaluation Pipeline

`CheckAccessAsync` will validate the input, load enabled policies in stable policy-id order, resolve resource type/subtyping, then evaluate every candidate as:

1. Parse each stored path into canonical relation names and find the first stable directed witness from subject to resource at `AsOf`.
2. Resolve ABAC references from subject/resource type or property values at the same temporal point.
3. Evaluate predicates; malformed references, missing values and unsupported operators yield false with explanation detail.
4. Collect matching allow/deny policies and field-hide effects. Final allow is at least one allow and no deny.

## Contracts

Extend the typed `CheckAccessResult` rather than forcing callers to parse JSON for field visibility. Preserve `Explanation` as a serializable, deterministic detailed record and keep cancellation on all store reads.

## Migration

This is D1's intentional hard cut. Existing path strings such as `subject->person` must be rewritten to canonical relation paths and backed by actual graph edges; they are no longer explanatory wildcard grants.

## Risks

- Security regression: missing/invalid data must never become an allow.
- Temporal inconsistency: all witness and attribute reads for one request must use the same normalized `AsOf`.
- Explanation drift: deterministic ordering is required for conformance tests and audit consumers.
