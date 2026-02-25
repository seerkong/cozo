# Wave WAVE-P1-02

- Phase: P1
- Task: T1.2 - alias mapping + canonicalize + read fallback
- Status: DONE

Planned work
- Implement resolveType/resolveRel/resolveAttr with cycle detection
- Canonicalize inputs on write APIs (define*, createEntity/upsertEntity, setProperty/linkEntities, etc.)
- Read path: canonical wins; if canonical missing but alias value exists, fallback to alias
- Update getEntityView to return canonical property keys
- Add Bun tests covering alias resolution, cycles, and canonical precedence

Notes
- commit_mode=manual: do not create git commits

Done
- Implemented resolveType/resolveRel/resolveAttr with cycle detection
- Canonicalized names on write paths and normalized outputs on read paths
- Added Bun tests: `cozo-lib-bun/__tests__/om-alias-resolution.test.js`
