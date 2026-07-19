const { describe, test, expect } = require('bun:test');

const { createTestDb } = require('./helpers');

async function withPermissionFixture(run) {
  const { db, om } = await createTestDb();
  try {
    await om.defineType(db, 'User', 'User');
    await om.defineType(db, 'Resource', 'Resource');
    await om.defineRelation(db, 'owns', 'User', 'Resource', true);
    await om.createEntity(db, 'user:1', 'User', 'User 1');
    await om.createEntity(db, 'resource:1', 'Resource', 'Resource 1');
    return await run({ db, om });
  } finally {
    db.close();
  }
}

function policy(policyId, action, effect = 'allow') {
  return {
    policy_id: policyId,
    effect,
    action,
    resource_type: 'Resource',
    enabled: true,
    description: policyId,
  };
}

describe('P1/T1.1: strict permission-governance conformance', () => {
  test('unwitnessed path denies with an unmatched path explanation', async () => {
    await withPermissionFixture(async ({ db, om }) => {
      await om.seedPermissionMetadata(db, {
        policies: [policy('policy:unwitnessed', 'read')],
        pathRules: [{ policy_id: 'policy:unwitnessed', path: 'owns' }],
      });

      const result = await om.checkAccess(db, { subjectId: 'user:1', action: 'read', resourceId: 'resource:1' });
      const evaluated = result.explanation.evaluatedPolicies.find((entry) => entry.policyId === 'policy:unwitnessed');
      expect(result.allow).toBe(false);
      expect(evaluated.path).toEqual({ rules: ['owns'], matched: false, matchedRule: null, witness: null });
    });
  });

  test('directed witness allows and records its canonical hop', async () => {
    await withPermissionFixture(async ({ db, om }) => {
      await om.linkEntities(db, 'user:1', 'owns', 'resource:1');
      await om.seedPermissionMetadata(db, {
        policies: [policy('policy:witness', 'read')],
        pathRules: [{ policy_id: 'policy:witness', path: '["owns"]' }],
      });

      const result = await om.checkAccess(db, { subjectId: 'user:1', action: 'read', resourceId: 'resource:1' });
      const evaluated = result.explanation.evaluatedPolicies.find((entry) => entry.policyId === 'policy:witness');
      expect(result.allow).toBe(true);
      expect(evaluated.path.witness).toEqual([{ fromId: 'user:1', relName: 'owns', toId: 'resource:1' }]);
    });
  });

  test('a JSON empty path witnesses only an identical subject and resource', async () => {
    await withPermissionFixture(async ({ db, om }) => {
      await om.seedPermissionMetadata(db, {
        policies: [
          { ...policy('policy:empty-self', 'empty-self'), resource_type: 'User' },
          policy('policy:empty-other', 'empty-other'),
        ],
        pathRules: [
          { policy_id: 'policy:empty-self', path: '[]' },
          { policy_id: 'policy:empty-other', path: '[]' },
        ],
      });

      const self = await om.checkAccess(db, {
        subjectId: 'user:1', action: 'empty-self', resourceId: 'user:1',
      });
      const other = await om.checkAccess(db, {
        subjectId: 'user:1', action: 'empty-other', resourceId: 'resource:1',
      });
      expect(self.allow).toBe(true);
      expect(self.explanation.evaluatedPolicies
        .find((entry) => entry.policyId === 'policy:empty-self').path.witness).toEqual([]);
      expect(other.allow).toBe(false);
      expect(other.explanation.evaluatedPolicies
        .find((entry) => entry.policyId === 'policy:empty-other').path.witness).toBeNull();
    });
  });

  test('asOf selects the graph state that contains the witness', async () => {
    await withPermissionFixture(async ({ db, om }) => {
      await om.linkEntities(db, 'user:1', 'owns', 'resource:1', null, { validTime: '2024-01-01T00:00:00Z' });
      await om.unlinkEntities(db, 'user:1', 'owns', 'resource:1', { validTime: '2025-01-01T00:00:00Z' });
      await om.seedPermissionMetadata(db, {
        policies: [policy('policy:temporal-graph', 'read')],
        pathRules: [{ policy_id: 'policy:temporal-graph', path: 'owns' }],
      });

      const historical = await om.checkAccess(db, {
        subjectId: 'user:1', action: 'read', resourceId: 'resource:1', asOf: '2024-06-01T00:00:00Z',
      });
      const afterRetract = await om.checkAccess(db, {
        subjectId: 'user:1', action: 'read', resourceId: 'resource:1', asOf: '2025-06-01T00:00:00Z',
      });
      expect(historical.allow).toBe(true);
      expect(afterRetract.allow).toBe(false);
      expect(historical.explanation.input.asOf).toBe('2024-06-01T00:00:00.000Z');
    });
  });

  test('wildcard action/resource policies neither grant nor deny without an explicit Bun match', async () => {
    await withPermissionFixture(async ({ db, om }) => {
      await om.linkEntities(db, 'user:1', 'owns', 'resource:1');
      await om.seedPermissionMetadata(db, {
        policies: [
          policy('policy:wildcard-action:allow', '*'),
          policy('policy:wildcard-action:deny', '*', 'deny'),
          policy('policy:wildcard-action:explicit', 'wildcard-action'),
          { ...policy('policy:wildcard-resource:allow', 'wildcard-resource-only'), resource_type: '*' },
          { ...policy('policy:wildcard-resource:deny', 'wildcard-resource', 'deny'), resource_type: '*' },
          policy('policy:wildcard-resource:explicit', 'wildcard-resource'),
        ],
        pathRules: [
          { policy_id: 'policy:wildcard-action:allow', path: 'owns' },
          { policy_id: 'policy:wildcard-action:deny', path: 'owns' },
          { policy_id: 'policy:wildcard-action:explicit', path: 'owns' },
          { policy_id: 'policy:wildcard-resource:allow', path: 'owns' },
          { policy_id: 'policy:wildcard-resource:deny', path: 'owns' },
          { policy_id: 'policy:wildcard-resource:explicit', path: 'owns' },
        ],
      });

      expect((await om.checkAccess(db, {
        subjectId: 'user:1', action: 'wildcard-action-only', resourceId: 'resource:1',
      })).allow).toBe(false);
      expect((await om.checkAccess(db, {
        subjectId: 'user:1', action: 'wildcard-action', resourceId: 'resource:1',
      })).allow).toBe(true);
      expect((await om.checkAccess(db, {
        subjectId: 'user:1', action: 'wildcard-resource-only', resourceId: 'resource:1',
      })).allow).toBe(false);
      expect((await om.checkAccess(db, {
        subjectId: 'user:1', action: 'wildcard-resource', resourceId: 'resource:1',
      })).allow).toBe(true);
    });
  });

  test('ABAC ordered comparator allows, then a witnessed deny overrides it', async () => {
    await withPermissionFixture(async ({ db, om }) => {
      await om.defineAttribute(db, 'User', 'role', 'String', false);
      await om.defineAttribute(db, 'User', 'clearance', 'Number', false);
      await om.linkEntities(db, 'user:1', 'owns', 'resource:1');
      await om.setProperty(db, 'user:1', 'role', 'admin');
      await om.setProperty(db, 'user:1', 'clearance', 7);
      await om.seedPermissionMetadata(db, {
        policies: [policy('policy:allow:admin', 'read'), policy('policy:deny:low-clearance', 'read', 'deny')],
        pathRules: [
          { policy_id: 'policy:allow:admin', path: 'owns' },
          { policy_id: 'policy:deny:low-clearance', path: 'owns' },
        ],
        abacRules: [
          { policy_id: 'policy:allow:admin', left_ref: 'subject.role', op: '==', right_ref: 'admin' },
          { policy_id: 'policy:allow:admin', left_ref: 'subject.clearance', op: '>=', right_ref: '5' },
          { policy_id: 'policy:deny:low-clearance', left_ref: 'subject.clearance', op: '<', right_ref: '5' },
        ],
      });

      expect((await om.checkAccess(db, { subjectId: 'user:1', action: 'read', resourceId: 'resource:1' })).allow).toBe(true);
      await om.setProperty(db, 'user:1', 'clearance', 3);
      const denied = await om.checkAccess(db, { subjectId: 'user:1', action: 'read', resourceId: 'resource:1' });
      expect(denied.allow).toBe(false);
      expect(denied.explanation.final.denyPolicies).toEqual(['policy:deny:low-clearance']);
    });
  });

  test('asOf resolves ABAC property values at the same temporal point as the witness', async () => {
    await withPermissionFixture(async ({ db, om }) => {
      await om.defineAttribute(db, 'User', 'role', 'String', false);
      await om.linkEntities(db, 'user:1', 'owns', 'resource:1', null, { validTime: '2024-01-01T00:00:00Z' });
      await om.setProperty(db, 'user:1', 'role', 'admin', { validTime: '2024-01-01T00:00:00Z' });
      await om.setProperty(db, 'user:1', 'role', 'viewer', { validTime: '2025-01-01T00:00:00Z' });
      await om.seedPermissionMetadata(db, {
        policies: [policy('policy:temporal-role', 'read')],
        pathRules: [{ policy_id: 'policy:temporal-role', path: 'owns' }],
        abacRules: [{ policy_id: 'policy:temporal-role', left_ref: 'subject.role', op: '==', right_ref: 'admin' }],
      });

      expect((await om.checkAccess(db, {
        subjectId: 'user:1', action: 'read', resourceId: 'resource:1', asOf: '2024-06-01T00:00:00Z',
      })).allow).toBe(true);
      expect((await om.checkAccess(db, {
        subjectId: 'user:1', action: 'read', resourceId: 'resource:1', asOf: '2025-06-01T00:00:00Z',
      })).allow).toBe(false);
    });
  });

  test('asOf is normalized once at the authorization boundary for witness and ABAC reads', async () => {
    await withPermissionFixture(async ({ db, om }) => {
      await om.defineAttribute(db, 'User', 'role', 'String', false);
      await om.linkEntities(db, 'user:1', 'owns', 'resource:1', null, { validTime: '2024-01-01T00:00:00Z' });
      await om.setProperty(db, 'user:1', 'role', 'admin', { validTime: '2024-01-01T00:00:00Z' });
      await om.seedPermissionMetadata(db, {
        policies: [policy('policy:single-normalization', 'read')],
        pathRules: [{ policy_id: 'policy:single-normalization', path: 'owns' }],
        abacRules: [{ policy_id: 'policy:single-normalization', left_ref: 'subject.role', op: '==', right_ref: 'admin' }],
      });

      const originalDateParse = Date.parse;
      let parseCount = 0;
      Date.parse = (...args) => {
        parseCount += 1;
        return originalDateParse(...args);
      };
      try {
        const result = await om.checkAccess(db, {
          subjectId: 'user:1', action: 'read', resourceId: 'resource:1', asOf: '2024-06-01',
        });
        expect(result.allow).toBe(true);
        expect(result.explanation.input.asOf).toBe('2024-06-01T00:00:00.000Z');
        expect(parseCount).toBe(1);
      } finally {
        Date.parse = originalDateParse;
      }
    });
  });

  test('a matched hide rule leaves access allowed and marks the field hidden', async () => {
    await withPermissionFixture(async ({ db, om }) => {
      await om.linkEntities(db, 'user:1', 'owns', 'resource:1');
      await om.seedPermissionMetadata(db, {
        policies: [policy('policy:hide-secret', 'read')],
        pathRules: [{ policy_id: 'policy:hide-secret', path: 'owns' }],
        abacRules: [{ policy_id: 'policy:hide-secret', left_ref: 'field.secret', op: 'hide', right_ref: 'true' }],
      });

      const result = await om.checkAccess(db, { subjectId: 'user:1', action: 'read', resourceId: 'resource:1' });
      expect(result.allow).toBe(true);
      expect(result.fieldVisibility).toEqual({ secret: 'hidden' });
    });
  });

  test('field hide is projected only from a matched allow policy', async () => {
    await withPermissionFixture(async ({ db, om }) => {
      await om.defineAttribute(db, 'User', 'role', 'String', false);
      await om.setProperty(db, 'user:1', 'role', 'viewer');
      await om.seedPermissionMetadata(db, {
        policies: [
          policy('policy:hide-unmatched', 'hide-unmatched'),
          policy('policy:hide-deny', 'hide-deny', 'deny'),
          policy('policy:hide-failed-abac', 'hide-failed-abac'),
          policy('policy:hide-allowed', 'hide-allowed'),
        ],
        pathRules: [
          { policy_id: 'policy:hide-unmatched', path: 'owns' },
          { policy_id: 'policy:hide-deny', path: 'owns' },
          { policy_id: 'policy:hide-failed-abac', path: 'owns' },
          { policy_id: 'policy:hide-allowed', path: 'owns' },
        ],
        abacRules: [
          { policy_id: 'policy:hide-unmatched', left_ref: 'field.unmatched', op: 'hide', right_ref: 'true' },
          { policy_id: 'policy:hide-deny', left_ref: 'field.denied', op: 'hide', right_ref: 'true' },
          { policy_id: 'policy:hide-failed-abac', left_ref: 'field.failed', op: 'hide', right_ref: 'true' },
          { policy_id: 'policy:hide-failed-abac', left_ref: 'subject.role', op: '==', right_ref: 'admin' },
          { policy_id: 'policy:hide-allowed', left_ref: 'field.allowed', op: 'hide', right_ref: 'true' },
        ],
      });

      const unmatched = await om.checkAccess(db, {
        subjectId: 'user:1', action: 'hide-unmatched', resourceId: 'resource:1',
      });
      expect(unmatched.allow).toBe(false);
      expect(unmatched.fieldVisibility).toBeUndefined();

      await om.linkEntities(db, 'user:1', 'owns', 'resource:1');
      const denied = await om.checkAccess(db, {
        subjectId: 'user:1', action: 'hide-deny', resourceId: 'resource:1',
      });
      const failedAbac = await om.checkAccess(db, {
        subjectId: 'user:1', action: 'hide-failed-abac', resourceId: 'resource:1',
      });
      const allowed = await om.checkAccess(db, {
        subjectId: 'user:1', action: 'hide-allowed', resourceId: 'resource:1',
      });
      expect(denied.allow).toBe(false);
      expect(denied.fieldVisibility).toBeUndefined();
      expect(failedAbac.allow).toBe(false);
      expect(failedAbac.fieldVisibility).toBeUndefined();
      expect(allowed.allow).toBe(true);
      expect(allowed.fieldVisibility).toEqual({ allowed: 'hidden' });
    });
  });

  test('unknown paths, unsupported operators, and malformed references fail closed with detail', async () => {
    await withPermissionFixture(async ({ db, om }) => {
      await om.linkEntities(db, 'user:1', 'owns', 'resource:1');
      await om.seedPermissionMetadata(db, {
        policies: [
          policy('policy:invalid-path', 'invalid-path'),
          policy('policy:invalid-operator', 'invalid-operator'),
          policy('policy:invalid-reference', 'invalid-reference'),
        ],
        pathRules: [
          { policy_id: 'policy:invalid-path', path: 'missing_relation' },
          { policy_id: 'policy:invalid-operator', path: 'owns' },
          { policy_id: 'policy:invalid-reference', path: 'owns' },
        ],
        abacRules: [
          { policy_id: 'policy:invalid-operator', left_ref: 'subject.type', op: 'contains', right_ref: 'User' },
          { policy_id: 'policy:invalid-reference', left_ref: 'subject.', op: '==', right_ref: 'User' },
        ],
      });

      const invalidPath = await om.checkAccess(db, { subjectId: 'user:1', action: 'invalid-path', resourceId: 'resource:1' });
      const invalidOperator = await om.checkAccess(db, { subjectId: 'user:1', action: 'invalid-operator', resourceId: 'resource:1' });
      const invalidReference = await om.checkAccess(db, { subjectId: 'user:1', action: 'invalid-reference', resourceId: 'resource:1' });
      expect(invalidPath.allow).toBe(false);
      expect(invalidOperator.allow).toBe(false);
      expect(invalidReference.allow).toBe(false);
      expect(invalidOperator.explanation.evaluatedPolicies
        .find((entry) => entry.policyId === 'policy:invalid-operator').abac.rules[0].error)
        .toContain("Unsupported ABAC op 'contains'");
      expect(invalidReference.explanation.evaluatedPolicies
        .find((entry) => entry.policyId === 'policy:invalid-reference').abac.rules[0].result)
        .toBe(false);
    });
  });

  test('compatibility ABAC references fail closed as malformed_reference', async () => {
    await withPermissionFixture(async ({ db, om }) => {
      await om.linkEntities(db, 'user:1', 'owns', 'resource:1');
      const prohibited = [
        ['subject-id', 'subject.id', 'user:1'],
        ['action', 'action', 'action'],
        ['resource-id', 'resource.id', 'resource:1'],
        ['resource-field', 'resource.field', 'classification'],
      ];
      await om.seedPermissionMetadata(db, {
        policies: prohibited.map(([name]) => policy(`policy:compat:${name}`, `compat-${name}`)),
        pathRules: prohibited.map(([name]) => ({ policy_id: `policy:compat:${name}`, path: 'owns' })),
        abacRules: prohibited.map(([name, leftRef, rightRef]) => ({
          policy_id: `policy:compat:${name}`, left_ref: leftRef, op: '==', right_ref: rightRef,
        })),
      });

      for (const [name] of prohibited) {
        const result = await om.checkAccess(db, {
          subjectId: 'user:1', action: `compat-${name}`, resourceId: 'resource:1', fieldName: 'classification',
        });
        const rule = result.explanation.evaluatedPolicies
          .find((entry) => entry.policyId === `policy:compat:${name}`).abac.rules[0];
        expect(result.allow).toBe(false);
        expect(rule.error).toBe('malformed_reference');
      }
    });
  });

  test('policy and explanation order is stable despite reverse seed order', async () => {
    await withPermissionFixture(async ({ db, om }) => {
      await om.linkEntities(db, 'user:1', 'owns', 'resource:1');
      await om.seedPermissionMetadata(db, {
        policies: [policy('policy:stable:z', 'read'), policy('policy:stable:a', 'read')],
        pathRules: [
          { policy_id: 'policy:stable:z', path: 'owns' },
          { policy_id: 'policy:stable:a', path: 'owns' },
        ],
      });

      const first = await om.checkAccess(db, { subjectId: 'user:1', action: 'read', resourceId: 'resource:1' });
      const second = await om.checkAccess(db, { subjectId: 'user:1', action: 'read', resourceId: 'resource:1' });
      expect(first.matchedPolicies.map((entry) => entry.policyId)).toEqual(['policy:stable:a', 'policy:stable:z']);
      expect(first.explanation).toEqual(second.explanation);
    });
  });
});
