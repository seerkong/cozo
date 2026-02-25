# Wave WAVE-P3-02

- Phase: P3
- Task: T3.2 - checkAccess + explanation
- Status: DONE

Planned work
- Implement `checkAccess(runner, { subjectId, action, resourceId, asOf? })`
- Relation-path rules (om_perm_path_rule) to constrain scope and return a witness path
- ABAC rules (om_perm_abac_rule) to refine allow/deny and field-level visibility
- Explanation output includes matched policies + witness + evaluated conditions
- Ensure policy references use alias resolution (types/attrs/rels) where applicable
- Add Bun tests

Notes
- commit_mode=manual: do not create git commits

Done
- Implemented `checkAccess(runner, { subjectId, action, resourceId, asOf? })` with hybrid evaluation:
  - Relation-path scope via `om_perm_path_rule` (JSON-array or slash-delimited path) and witness hop output
  - ABAC rules via `om_perm_abac_rule` with basic ops and field-level hide (`field.<name>` + `hide`)
  - Policy selection via `om_perm_policy` with deny override and resource_type subtype checks
  - Alias integration via resolveType/resolveRel/resolveAttr
- Added Bun tests covering witness, allow/deny precedence, and field visibility
