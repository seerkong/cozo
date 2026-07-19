const { expect, test, describe } = require('bun:test');

const { createTestDb } = require('./helpers');
const dsl = require('../cozo-dsl');

describe('Phase 2 (track add-action-and-constraints): actions', () => {
  test('defineAction stores metadata in om_action_def', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Resource', 'Resource');

      await om.defineAction(
        db,
        'Resource',
        'approve',
        async () => [],
        'Approve resource'
      );

      const q = dsl.query()
        .select(['description'])
        .fromStored('om_action_def', {
          type_name: dsl.param('type_name', 'Resource'),
          action_name: dsl.param('action_name', 'approve'),
          description: dsl.var('description'),
        })
        .limit(1)
        .build();
      const result = await db.run(q.script, q.params);
      expect(result.rows.length).toBe(1);
      expect(result.rows[0][0]).toBe('Approve resource');
    } finally {
      db.close();
    }
  });

  test('executeAction runs handler and applies returned mutations', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Resource', 'Resource');
      await om.defineAttribute(db, 'Resource', 'status', 'String', true);
      await om.createEntity(db, 'resource:1', 'Resource', 'Resource #1');
      await om.setProperty(db, 'resource:1', 'status', 'pending');

      await om.defineMutation(
        db,
        'Resource',
        'setStatus',
        async (ctx, params) => {
          await ctx.setProperty('status', String(params.status));
        }
      );

      await om.defineAction(
        db,
        'Resource',
        'approve',
        async () => [{ mutation: 'setStatus', params: { status: 'approved' } }]
      );

      await om.executeAction(db, 'resource:1', 'approve', {});
      const status = await om.getProperty(db, 'resource:1', 'status');
      expect(status).toBe('approved');
    } finally {
      db.close();
    }
  });

  test('executeAction errors if action is not defined', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Resource', 'Resource');
      await om.createEntity(db, 'resource:2', 'Resource', 'Resource #2');

      await expect(om.executeAction(db, 'resource:2', 'reject', {})).rejects.toThrow(
        /Action 'reject' not defined|Action not defined|does not exist/i
      );
    } finally {
      db.close();
    }
  });

  test('action inheritance: subtype can execute parent action', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Resource', 'Resource');
      await om.defineType(db, 'ExecutableResource', 'ExecutableResource', { parentType: 'Resource' });
      await om.defineAttribute(db, 'Resource', 'status', 'String', true);

      await om.createEntity(db, 'exec:1', 'ExecutableResource', 'ExecutableResource #1');
      await om.setProperty(db, 'exec:1', 'status', 'pending');

      await om.defineMutation(db, 'Resource', 'setStatus', async (ctx, params) => {
        await ctx.setProperty('status', String(params.status));
      });

      await om.defineAction(db, 'Resource', 'decommission', async () => [
        { mutation: 'setStatus', params: { status: 'decommissioned' } },
      ]);

      await om.executeAction(db, 'exec:1', 'decommission', {});
      expect(await om.getProperty(db, 'exec:1', 'status')).toBe('decommissioned');
    } finally {
      db.close();
    }
  });

  test('action override: subtype can callParentAction and extend mutations', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Resource', 'Resource');
      await om.defineType(db, 'ExecutableResource', 'ExecutableResource', { parentType: 'Resource' });

      await om.defineAttribute(db, 'Resource', 'status', 'String', true);
      await om.defineAttribute(db, 'ExecutableResource', 'ip_address', 'String', false);

      await om.createEntity(db, 'exec:2', 'ExecutableResource', 'ExecutableResource #2');
      await om.setProperty(db, 'exec:2', 'status', 'pending');
      await om.setProperty(db, 'exec:2', 'ip_address', '10.0.0.1');

      await om.defineMutation(db, 'Resource', 'setStatus', async (ctx, params) => {
        await ctx.setProperty('status', String(params.status));
      });
      await om.defineMutation(db, 'ExecutableResource', 'clearIp', async (ctx) => {
        await ctx.setProperty('ip_address', '');
      });

      await om.defineAction(db, 'Resource', 'decommission', async () => [
        { mutation: 'setStatus', params: { status: 'decommissioned' } },
      ]);

      await om.defineAction(db, 'ExecutableResource', 'decommission', async (ctx, params) => {
        const parent = await ctx.callParentAction('decommission', params);
        return [...parent, { mutation: 'clearIp', params: {} }];
      });

      await om.executeAction(db, 'exec:2', 'decommission', {});
      expect(await om.getProperty(db, 'exec:2', 'status')).toBe('decommissioned');
      expect(await om.getProperty(db, 'exec:2', 'ip_address')).toBe('');
    } finally {
      db.close();
    }
  });

  test('nearest ancestor action is used when child does not override', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Resource', 'Resource');
      await om.defineType(db, 'ExecutableResource', 'ExecutableResource', { parentType: 'Resource' });
      await om.defineType(db, 'ApiService', 'ApiService', { parentType: 'ExecutableResource' });

      await om.defineAttribute(db, 'Resource', 'status', 'String', true);
      await om.defineAttribute(db, 'ExecutableResource', 'ip_address', 'String', false);

      await om.createEntity(db, 'srv:1', 'ApiService', 'ApiService #1');
      await om.setProperty(db, 'srv:1', 'status', 'pending');
      await om.setProperty(db, 'srv:1', 'ip_address', '10.0.0.2');

      await om.defineMutation(db, 'Resource', 'setStatus', async (ctx, params) => {
        await ctx.setProperty('status', String(params.status));
      });
      await om.defineMutation(db, 'ExecutableResource', 'clearIp', async (ctx) => {
        await ctx.setProperty('ip_address', '');
      });

      await om.defineAction(db, 'Resource', 'decommission', async () => [
        { mutation: 'setStatus', params: { status: 'decommissioned' } },
      ]);

      await om.defineAction(db, 'ExecutableResource', 'decommission', async (ctx, params) => {
        const parent = await ctx.callParentAction('decommission', params);
        return [...parent, { mutation: 'clearIp', params: {} }];
      });

      // ApiService does NOT override, so it should use ExecutableResource's nearest-ancestor version.
      await om.executeAction(db, 'srv:1', 'decommission', {});
      expect(await om.getProperty(db, 'srv:1', 'status')).toBe('decommissioned');
      expect(await om.getProperty(db, 'srv:1', 'ip_address')).toBe('');
    } finally {
      db.close();
    }
  });

  test('three-level action overrides call each owner and commit parent and child mutations once', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Root', 'Root');
      await om.defineType(db, 'Middle', 'Middle', { parentType: 'Root' });
      await om.defineType(db, 'Leaf', 'Leaf', { parentType: 'Middle' });

      await om.defineAttribute(db, 'Root', 'parent_effect', 'String', false);
      await om.defineAttribute(db, 'Leaf', 'child_effect', 'String', false);
      await om.createEntity(db, 'leaf:three-level', 'Leaf', 'Three-level leaf');

      const actionOwners = [];
      const mutationCalls = [];

      await om.defineMutation(db, 'Root', 'applyParentEffect', async (ctx) => {
        mutationCalls.push('parent');
        await ctx.setProperty('parent_effect', 'committed');
      });
      await om.defineMutation(db, 'Leaf', 'applyChildEffect', async (ctx) => {
        mutationCalls.push('child');
        await ctx.setProperty('child_effect', 'committed');
      });

      await om.defineAction(db, 'Root', 'cascade', async (ctx) => {
        actionOwners.push(ctx.actionOwnerType);
        return [{ mutation: 'applyParentEffect', params: {} }];
      });
      await om.defineAction(db, 'Middle', 'cascade', async (ctx, params) => {
        actionOwners.push(ctx.actionOwnerType);
        return ctx.callParentAction('cascade', params);
      });
      await om.defineAction(db, 'Leaf', 'cascade', async (ctx, params) => {
        actionOwners.push(ctx.actionOwnerType);
        const parent = await ctx.callParentAction('cascade', params);
        return [...parent, { mutation: 'applyChildEffect', params: {} }];
      });

      await om.executeAction(db, 'leaf:three-level', 'cascade', {});

      expect(actionOwners).toEqual(['Leaf', 'Middle', 'Root']);
      expect(mutationCalls).toEqual(['parent', 'child']);
      expect(await om.getProperty(db, 'leaf:three-level', 'parent_effect')).toBe('committed');
      expect(await om.getProperty(db, 'leaf:three-level', 'child_effect')).toBe('committed');
    } finally {
      db.close();
    }
  });

  test('three-level parent and child mutations roll back when a later mutation fails', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Root', 'Root');
      await om.defineType(db, 'Middle', 'Middle', { parentType: 'Root' });
      await om.defineType(db, 'Leaf', 'Leaf', { parentType: 'Middle' });

      await om.defineAttribute(db, 'Root', 'parent_effect', 'String', false);
      await om.defineAttribute(db, 'Leaf', 'child_effect', 'String', false);
      await om.createEntity(db, 'leaf:rollback', 'Leaf', 'Rollback leaf');
      await om.setProperty(db, 'leaf:rollback', 'parent_effect', 'initial-parent');
      await om.setProperty(db, 'leaf:rollback', 'child_effect', 'initial-child');

      await om.defineMutation(db, 'Root', 'applyParentEffect', async (ctx) => {
        await ctx.setProperty('parent_effect', 'changed-parent');
      });
      await om.defineMutation(db, 'Leaf', 'applyChildEffect', async (ctx) => {
        await ctx.setProperty('child_effect', 'changed-child');
      });
      await om.defineMutation(db, 'Leaf', 'failAfterEffects', async () => {
        throw new Error('later mutation failed');
      });

      await om.defineAction(db, 'Root', 'cascade', async () => [
        { mutation: 'applyParentEffect', params: {} },
      ]);
      await om.defineAction(db, 'Middle', 'cascade', async (ctx, params) =>
        ctx.callParentAction('cascade', params)
      );
      await om.defineAction(db, 'Leaf', 'cascade', async (ctx, params) => {
        const parent = await ctx.callParentAction('cascade', params);
        return [
          ...parent,
          { mutation: 'applyChildEffect', params: {} },
          { mutation: 'failAfterEffects', params: {} },
        ];
      });

      await expect(om.executeAction(db, 'leaf:rollback', 'cascade', {})).rejects.toThrow(
        'later mutation failed'
      );
      expect(await om.getProperty(db, 'leaf:rollback', 'parent_effect')).toBe('initial-parent');
      expect(await om.getProperty(db, 'leaf:rollback', 'child_effect')).toBe('initial-child');
    } finally {
      db.close();
    }
  });

  test('child action resolves an ancestor mutation from the nearest owner', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Root', 'Root');
      await om.defineType(db, 'Middle', 'Middle', { parentType: 'Root' });
      await om.defineType(db, 'Leaf', 'Leaf', { parentType: 'Middle' });
      await om.defineAttribute(db, 'Root', 'mutation_owner', 'String', false);
      await om.createEntity(db, 'leaf:mutation-owner', 'Leaf', 'Mutation owner leaf');

      const mutationOwners = [];
      await om.defineMutation(db, 'Root', 'recordOwner', async (ctx) => {
        mutationOwners.push('Root');
        await ctx.setProperty('mutation_owner', 'Root');
      });
      await om.defineMutation(db, 'Middle', 'recordOwner', async (ctx) => {
        mutationOwners.push('Middle');
        await ctx.setProperty('mutation_owner', 'Middle');
      });
      await om.defineAction(db, 'Leaf', 'recordMutationOwner', async () => [
        { mutation: 'recordOwner', params: {} },
      ]);

      await om.executeAction(db, 'leaf:mutation-owner', 'recordMutationOwner', {});

      expect(mutationOwners).toEqual(['Middle']);
      expect(await om.getProperty(db, 'leaf:mutation-owner', 'mutation_owner')).toBe('Middle');
    } finally {
      db.close();
    }
  });
});
