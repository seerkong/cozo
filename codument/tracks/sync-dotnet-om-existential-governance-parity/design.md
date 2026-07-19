# Design

Definition normalization accepts only `=`, `!=`, `>`, `>=`, `<`, and `<=`.
Persisted rules remain historical metadata; each evaluation re-resolves the
body type, relation, target type, and where attributes through current aliases.
Missing rule storage is treated as an empty rule set for list/check/apply.
