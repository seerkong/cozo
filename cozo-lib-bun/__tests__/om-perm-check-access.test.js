const { describe, test, expect } = require('bun:test');

const { createTestDb } = require('./helpers');

describe('P3/WAVE-P3-02 (T3.2): checkAccess (hybrid path + ABAC) + explanation', () => {
  test('path witness: subject -owns-> resource yields allow + witness hop', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'User', 'User');
      await om.defineType(db, 'Asset', 'Asset');
      await om.defineRelation(db, 'owns', 'User', 'Asset', true);

      await om.createEntity(db, 'u:1', 'User', 'User 1');
      await om.createEntity(db, 'a:1', 'Asset', 'Asset 1');
      await om.linkEntities(db, 'u:1', 'owns', 'a:1');

      await om.seedPermissionMetadata(db, {
        policies: [
          {
            policy_id: 'pol:allow:owns',
            effect: 'allow',
            action: 'read',
            resource_type: 'Asset',
            enabled: true,
            description: 'Allow read when owns',
          },
        ],
        pathRules: [{ policy_id: 'pol:allow:owns', path: '["owns"]' }],
      });

      expect(typeof om.checkAccess).toBe('function');
      const result = await om.checkAccess(db, {
        subjectId: 'u:1',
        action: 'read',
        resourceId: 'a:1',
      });

      expect(result.allow).toBe(true);
      expect(Array.isArray(result.matchedPolicies)).toBe(true);
      expect(result.matchedPolicies.length).toBe(1);
      expect(result.explanation && typeof result.explanation === 'object').toBe(true);

      const evalPol = (result.explanation.evaluatedPolicies || []).find((p) => p.policyId === 'pol:allow:owns');
      expect(evalPol).toBeTruthy();
      expect(evalPol.path && evalPol.path.matched).toBe(true);
      expect(evalPol.path.witness).toEqual([{ fromId: 'u:1', relName: 'owns', toId: 'a:1' }]);
    } finally {
      db.close();
    }
  });

  test('ABAC allow/deny: deny overrides allow when subject.location != HQ', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'User', 'User');
      await om.defineType(db, 'Asset', 'Asset');
      await om.defineRelation(db, 'owns', 'User', 'Asset', true);
      await om.defineAttribute(db, 'User', 'role', 'String', false);
      await om.defineAttribute(db, 'User', 'location', 'String', false);

      await om.createEntity(db, 'u:1', 'User', 'User 1');
      await om.createEntity(db, 'a:1', 'Asset', 'Asset 1');
      await om.linkEntities(db, 'u:1', 'owns', 'a:1');

      await om.seedPermissionMetadata(db, {
        policies: [
          {
            policy_id: 'pol:allow:admin',
            effect: 'allow',
            action: 'read',
            resource_type: 'Asset',
            enabled: true,
            description: 'Allow admin',
          },
          {
            policy_id: 'pol:deny:not-hq',
            effect: 'deny',
            action: 'read',
            resource_type: 'Asset',
            enabled: true,
            description: 'Deny if not HQ',
          },
        ],
        pathRules: [
          { policy_id: 'pol:allow:admin', path: 'owns' },
          { policy_id: 'pol:deny:not-hq', path: 'owns' },
        ],
        abacRules: [
          { policy_id: 'pol:allow:admin', left_ref: 'subject.role', op: '==', right_ref: 'admin' },
          { policy_id: 'pol:deny:not-hq', left_ref: 'subject.location', op: '!=', right_ref: 'HQ' },
        ],
      });

      await om.setProperty(db, 'u:1', 'role', 'admin');
      await om.setProperty(db, 'u:1', 'location', 'HQ');

      const ok = await om.checkAccess(db, { subjectId: 'u:1', action: 'read', resourceId: 'a:1' });
      expect(ok.allow).toBe(true);

      await om.setProperty(db, 'u:1', 'location', 'Remote');
      const denied = await om.checkAccess(db, { subjectId: 'u:1', action: 'read', resourceId: 'a:1' });
      expect(denied.allow).toBe(false);
      const denyMatched = (denied.matchedPolicies || []).some((p) => p.policyId === 'pol:deny:not-hq' && p.effect === 'deny');
      expect(denyMatched).toBe(true);
    } finally {
      db.close();
    }
  });

  test('field-level hide: ABAC rule field.secret hide true sets fieldVisibility.secret=hidden', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'User', 'User');
      await om.defineType(db, 'Asset', 'Asset');
      await om.defineRelation(db, 'owns', 'User', 'Asset', true);

      await om.createEntity(db, 'u:1', 'User', 'User 1');
      await om.createEntity(db, 'a:1', 'Asset', 'Asset 1');
      await om.linkEntities(db, 'u:1', 'owns', 'a:1');

      await om.seedPermissionMetadata(db, {
        policies: [
          {
            policy_id: 'pol:allow:hide-secret',
            effect: 'allow',
            action: 'read',
            resource_type: 'Asset',
            enabled: true,
            description: 'Allow but hide secret',
          },
        ],
        pathRules: [{ policy_id: 'pol:allow:hide-secret', path: 'owns' }],
        abacRules: [{ policy_id: 'pol:allow:hide-secret', left_ref: 'field.secret', op: 'hide', right_ref: 'true' }],
      });

      const result = await om.checkAccess(db, { subjectId: 'u:1', action: 'read', resourceId: 'a:1' });
      expect(result.allow).toBe(true);
      expect(result.fieldVisibility).toEqual({ secret: 'hidden' });
    } finally {
      db.close();
    }
  });
});
