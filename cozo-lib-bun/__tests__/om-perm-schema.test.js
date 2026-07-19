const { describe, test, expect } = require('bun:test');

const dsl = require('../cozo-dsl');
const { createTestDb } = require('./helpers');

async function assertStoredRelationExists(db, relName, bindings) {
  const built = dsl
    .query()
    .select(['x'])
    .fromStored(relName, bindings)
    .atom('x = 1')
    .limit(1)
    .build();
  await expect(db.run(built.script, built.params)).resolves.toBeTruthy();
}

describe('P3/WAVE-P3-01 (T3.1.1): permission metadata schema', () => {
  test('initSchema creates stored relations for permission metadata', async () => {
    const { db } = await createTestDb();
    try {
      await assertStoredRelationExists(db, 'om_perm_action', {
        action: dsl.var('action'),
        description: dsl.var('_description'),
      });

      await assertStoredRelationExists(db, 'om_perm_policy', {
        policy_id: dsl.var('policy_id'),
        effect: dsl.var('_effect'),
        action: dsl.var('_action'),
        resource_type: dsl.var('_resource_type'),
        enabled: dsl.var('_enabled'),
        description: dsl.var('_description'),
      });

      await assertStoredRelationExists(db, 'om_perm_abac_rule', {
        policy_id: dsl.var('policy_id'),
        left_ref: dsl.var('left_ref'),
        op: dsl.var('op'),
        right_ref: dsl.var('right_ref'),
      });

      await assertStoredRelationExists(db, 'om_perm_path_rule', {
        policy_id: dsl.var('policy_id'),
        path: dsl.var('path'),
      });
    } finally {
      db.close();
    }
  });

  test('optional: can seed and read back permission metadata', async () => {
    const { db, om } = await createTestDb();
    try {
      if (!om || typeof om.seedPermissionMetadata !== 'function') {
        // Keep test tolerant for older builds without the seed helper.
        return;
      }

      await om.seedPermissionMetadata(db, {
        actions: [{ action: 'read:resource', description: 'Read resources' }],
        policies: [
          {
            policy_id: 'pol:1',
            effect: 'allow',
            action: 'read:resource',
            resource_type: 'Resource',
            enabled: true,
            description: 'Allow reading resources',
          },
        ],
        abacRules: [
          {
            policy_id: 'pol:1',
            left_ref: 'subject.role',
            op: '==',
            right_ref: 'admin',
          },
        ],
        pathRules: [{ policy_id: 'pol:1', path: '/resources/*' }],
      });

      const qAction = dsl
        .query()
        .select(['description'])
        .fromStored('om_perm_action', {
          action: dsl.param('action', 'read:resource'),
          description: dsl.var('description'),
        })
        .limit(1)
        .build();
      const rAction = await db.run(qAction.script, qAction.params);
      expect(rAction.rows.length).toBe(1);
      expect(rAction.rows[0][0]).toBe('Read resources');

      const qPolicy = dsl
        .query()
        .select(['effect', 'action', 'resource_type', 'enabled', 'description'])
        .fromStored('om_perm_policy', {
          policy_id: dsl.param('policy_id', 'pol:1'),
          effect: dsl.var('effect'),
          action: dsl.var('action'),
          resource_type: dsl.var('resource_type'),
          enabled: dsl.var('enabled'),
          description: dsl.var('description'),
        })
        .limit(1)
        .build();
      const rPolicy = await db.run(qPolicy.script, qPolicy.params);
      expect(rPolicy.rows.length).toBe(1);
      expect(rPolicy.rows[0][0]).toBe('allow');
      expect(rPolicy.rows[0][1]).toBe('read:resource');
      expect(rPolicy.rows[0][2]).toBe('Resource');
      expect(rPolicy.rows[0][3] === true || rPolicy.rows[0][3] === 1).toBe(true);
      expect(rPolicy.rows[0][4]).toBe('Allow reading resources');

      const qAbac = dsl
        .query()
        .select(['policy_id', 'left_ref', 'op', 'right_ref'])
        .fromStored('om_perm_abac_rule', {
          policy_id: dsl.var('policy_id'),
          left_ref: dsl.var('left_ref'),
          op: dsl.var('op'),
          right_ref: dsl.var('right_ref'),
        })
        .where(
          dsl.and(
            dsl.eq('policy_id', dsl.param('expected_policy_id', 'pol:1')),
            dsl.eq('left_ref', dsl.param('expected_left_ref', 'subject.role')),
            dsl.eq('op', dsl.param('expected_op', '==')),
            dsl.eq('right_ref', dsl.param('expected_right_ref', 'admin'))
          )
        )
        .limit(1)
        .build();
      const rAbac = await db.run(qAbac.script, qAbac.params);
      expect(rAbac.rows.length).toBe(1);
      expect(rAbac.rows[0][0]).toBe('pol:1');

      const qPath = dsl
        .query()
        .select(['policy_id', 'path'])
        .fromStored('om_perm_path_rule', {
          policy_id: dsl.var('policy_id'),
          path: dsl.var('path'),
        })
        .where(
          dsl.and(
            dsl.eq('policy_id', dsl.param('expected_policy_id_path', 'pol:1')),
            dsl.eq('path', dsl.param('expected_path', '/resources/*'))
          )
        )
        .limit(1)
        .build();
      const rPath = await db.run(qPath.script, qPath.params);
      expect(rPath.rows.length).toBe(1);
      expect(rPath.rows[0][0]).toBe('pol:1');
    } finally {
      db.close();
    }
  });
});
