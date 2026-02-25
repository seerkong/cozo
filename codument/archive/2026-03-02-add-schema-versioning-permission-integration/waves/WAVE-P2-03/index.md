# Wave WAVE-P2-03

- Phase: P2
- Task: T2.3 - rollbackSchema (strict/force)
- Status: DONE

Planned work
- Implement `rollbackSchema(runner, targetVersion, { strict })`
- Restore schema defs + alias mappings + perm policy metadata (om_perm_* when present)
- strict=true: validate current instance data against target schema; abort rollback with diagnostics
- strict=false: allow rollback and return diagnostics summary
- Add Bun tests

Notes
- commit_mode=manual: do not create git commits

Done
- Implemented `rollbackSchema(runner, targetVersion, options)` with write-tx support (multiTransact when available)
- Restores schema defs + alias mappings from `om_schema_snapshot` for the target version
- Restores `om_perm_*` policy metadata when present; skips gracefully if relations are missing
- Strict mode validates existing entities against restored schema and aborts rollback with concise diagnostics
- Force mode commits rollback and returns diagnostics
- Added Bun tests for strict/force rollback and alias mapping restoration
- Updated typings to export `rollbackSchema` signature
- Verified: `bun test` in `cozo-lib-bun` passes
