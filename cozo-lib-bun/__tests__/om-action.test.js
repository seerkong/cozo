const { expect, test, describe } = require('bun:test');

const { createTestDb } = require('./helpers');
const dsl = require('../cozo-dsl');

describe('Phase 2 (track add-action-and-constraints): actions', () => {
  test('defineAction stores metadata in om_action_def', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Asset', 'Asset');

      await om.defineAction(
        db,
        'Asset',
        'approve',
        async () => [],
        'Approve asset'
      );

      const q = dsl.query()
        .select(['description'])
        .fromStored('om_action_def', {
          type_name: dsl.param('type_name', 'Asset'),
          action_name: dsl.param('action_name', 'approve'),
          description: dsl.var('description'),
        })
        .limit(1)
        .build();
      const result = await db.run(q.script, q.params);
      expect(result.rows.length).toBe(1);
      expect(result.rows[0][0]).toBe('Approve asset');
    } finally {
      db.close();
    }
  });

  test('executeAction runs handler and applies returned mutations', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Asset', 'Asset');
      await om.defineAttribute(db, 'Asset', 'status', 'String', true);
      await om.createEntity(db, 'asset:1', 'Asset', 'Asset #1');
      await om.setProperty(db, 'asset:1', 'status', 'pending');

      await om.defineMutation(
        db,
        'Asset',
        'setStatus',
        async (ctx, params) => {
          await ctx.setProperty('status', String(params.status));
        }
      );

      await om.defineAction(
        db,
        'Asset',
        'approve',
        async () => [{ mutation: 'setStatus', params: { status: 'approved' } }]
      );

      await om.executeAction(db, 'asset:1', 'approve', {});
      const status = await om.getProperty(db, 'asset:1', 'status');
      expect(status).toBe('approved');
    } finally {
      db.close();
    }
  });

  test('executeAction errors if action is not defined', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Asset', 'Asset');
      await om.createEntity(db, 'asset:2', 'Asset', 'Asset #2');

      await expect(om.executeAction(db, 'asset:2', 'reject', {})).rejects.toThrow(
        /Action 'reject' not defined|Action not defined|does not exist/i
      );
    } finally {
      db.close();
    }
  });

  test('action inheritance: subtype can execute parent action', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Asset', 'Asset');
      await om.defineType(db, 'ITAsset', 'ITAsset', { parentType: 'Asset' });
      await om.defineAttribute(db, 'Asset', 'status', 'String', true);

      await om.createEntity(db, 'it:1', 'ITAsset', 'ITAsset #1');
      await om.setProperty(db, 'it:1', 'status', 'pending');

      await om.defineMutation(db, 'Asset', 'setStatus', async (ctx, params) => {
        await ctx.setProperty('status', String(params.status));
      });

      await om.defineAction(db, 'Asset', 'decommission', async () => [
        { mutation: 'setStatus', params: { status: 'decommissioned' } },
      ]);

      await om.executeAction(db, 'it:1', 'decommission', {});
      expect(await om.getProperty(db, 'it:1', 'status')).toBe('decommissioned');
    } finally {
      db.close();
    }
  });

  test('action override: subtype can callParentAction and extend mutations', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Asset', 'Asset');
      await om.defineType(db, 'ITAsset', 'ITAsset', { parentType: 'Asset' });

      await om.defineAttribute(db, 'Asset', 'status', 'String', true);
      await om.defineAttribute(db, 'ITAsset', 'ip_address', 'String', false);

      await om.createEntity(db, 'it:2', 'ITAsset', 'ITAsset #2');
      await om.setProperty(db, 'it:2', 'status', 'pending');
      await om.setProperty(db, 'it:2', 'ip_address', '10.0.0.1');

      await om.defineMutation(db, 'Asset', 'setStatus', async (ctx, params) => {
        await ctx.setProperty('status', String(params.status));
      });
      await om.defineMutation(db, 'ITAsset', 'clearIp', async (ctx) => {
        await ctx.setProperty('ip_address', '');
      });

      await om.defineAction(db, 'Asset', 'decommission', async () => [
        { mutation: 'setStatus', params: { status: 'decommissioned' } },
      ]);

      await om.defineAction(db, 'ITAsset', 'decommission', async (ctx, params) => {
        const parent = await ctx.callParentAction('decommission', params);
        return [...parent, { mutation: 'clearIp', params: {} }];
      });

      await om.executeAction(db, 'it:2', 'decommission', {});
      expect(await om.getProperty(db, 'it:2', 'status')).toBe('decommissioned');
      expect(await om.getProperty(db, 'it:2', 'ip_address')).toBe('');
    } finally {
      db.close();
    }
  });

  test('nearest ancestor action is used when child does not override', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Asset', 'Asset');
      await om.defineType(db, 'ITAsset', 'ITAsset', { parentType: 'Asset' });
      await om.defineType(db, 'Server', 'Server', { parentType: 'ITAsset' });

      await om.defineAttribute(db, 'Asset', 'status', 'String', true);
      await om.defineAttribute(db, 'ITAsset', 'ip_address', 'String', false);

      await om.createEntity(db, 'srv:1', 'Server', 'Server #1');
      await om.setProperty(db, 'srv:1', 'status', 'pending');
      await om.setProperty(db, 'srv:1', 'ip_address', '10.0.0.2');

      await om.defineMutation(db, 'Asset', 'setStatus', async (ctx, params) => {
        await ctx.setProperty('status', String(params.status));
      });
      await om.defineMutation(db, 'ITAsset', 'clearIp', async (ctx) => {
        await ctx.setProperty('ip_address', '');
      });

      await om.defineAction(db, 'Asset', 'decommission', async () => [
        { mutation: 'setStatus', params: { status: 'decommissioned' } },
      ]);

      await om.defineAction(db, 'ITAsset', 'decommission', async (ctx, params) => {
        const parent = await ctx.callParentAction('decommission', params);
        return [...parent, { mutation: 'clearIp', params: {} }];
      });

      // Server does NOT override, so it should use ITAsset's nearest-ancestor version.
      await om.executeAction(db, 'srv:1', 'decommission', {});
      expect(await om.getProperty(db, 'srv:1', 'status')).toBe('decommissioned');
      expect(await om.getProperty(db, 'srv:1', 'ip_address')).toBe('');
    } finally {
      db.close();
    }
  });
});
