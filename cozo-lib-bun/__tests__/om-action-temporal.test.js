const { expect, test, describe } = require('bun:test');

const { createTestDb } = require('./helpers');

describe('Phase 3 (track add-temporal-dimension): ActionContext temporal integration', () => {
  test('ctx.setProperty forwards validTime options', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Asset', 'Asset');
      await om.defineAttribute(db, 'Asset', 'status', 'String', false);
      await om.createEntity(db, 'asset:1', 'Asset', 'Asset #1');

      await om.defineAction(db, 'Asset', 'approve_at_2000', async () => [
        { mutation: 'setStatusAt', params: { status: 'approved', validTime: '2000-01-01T00:00:00Z' } },
      ]);

      await om.defineMutation(db, 'Asset', 'setStatusAt', async (ctx, params) => {
        await ctx.setProperty('status', String(params.status), { validTime: String(params.validTime) });
      });

      await om.executeAction(db, 'asset:1', 'approve_at_2000', {});

      const before = await om.getPropertyAsOf(db, 'asset:1', 'status', '1999-06-01T00:00:00Z');
      expect(before).toBeUndefined();
      const after = await om.getPropertyAsOf(db, 'asset:1', 'status', '2000-06-01T00:00:00Z');
      expect(after).toBe('approved');
    } finally {
      db.close();
    }
  });

  test('ctx.getProperty supports { asOf }', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Asset', 'Asset');
      await om.defineAttribute(db, 'Asset', 'status', 'String', false);
      await om.defineAttribute(db, 'Asset', 'status_at_2000', 'String', false);
      await om.createEntity(db, 'asset:2', 'Asset', 'Asset #2');

      await om.setProperty(db, 'asset:2', 'status', 'pending', { validTime: '1999-01-01T00:00:00Z' });
      await om.setProperty(db, 'asset:2', 'status', 'approved', { validTime: '2001-01-01T00:00:00Z' });

      await om.defineAction(db, 'Asset', 'snapshot_status', async () => [
        { mutation: 'copyStatusAt', params: { asOf: '2000-06-01T00:00:00Z' } },
      ]);
      await om.defineMutation(db, 'Asset', 'copyStatusAt', async (ctx, params) => {
        const v = await ctx.getProperty('status', { asOf: String(params.asOf) });
        await ctx.setProperty('status_at_2000', v);
      });

      await om.executeAction(db, 'asset:2', 'snapshot_status', {});
      expect(await om.getProperty(db, 'asset:2', 'status_at_2000')).toBe('pending');
    } finally {
      db.close();
    }
  });

  test('constraint validation in actions uses latest effective values (@ NOW)', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Asset', 'Asset');
      await om.defineAttribute(db, 'Asset', 'status', 'String', false);
      await om.defineAttribute(db, 'Asset', 'end_date', 'String', false);
      await om.createEntity(db, 'asset:3', 'Asset', 'Asset #3');
      await om.setProperty(db, 'asset:3', 'status', 'pending');

      await om.defineConstraint(db, 'Asset', 'active_requires_no_end_date', {
        when: async (ctx) => (await ctx.getProperty('status')) === 'active',
        then: async (ctx) => (await ctx.getProperty('end_date')) == null,
        message: 'end_date must be empty when status is active',
      });

      await om.defineAction(db, 'Asset', 'invalidate', async () => [
        { mutation: 'setStatus', params: { status: 'active' } },
        { mutation: 'setEndDate', params: { end_date: '2026-12-31' } },
      ]);
      await om.defineMutation(db, 'Asset', 'setStatus', async (ctx, params) => {
        await ctx.setProperty('status', String(params.status));
      });
      await om.defineMutation(db, 'Asset', 'setEndDate', async (ctx, params) => {
        await ctx.setProperty('end_date', String(params.end_date));
      });

      await expect(om.executeAction(db, 'asset:3', 'invalidate', {})).rejects.toThrow(
        /end_date must be empty|active_requires_no_end_date/i
      );
      expect(await om.getProperty(db, 'asset:3', 'status')).toBe('pending');
      expect(await om.getProperty(db, 'asset:3', 'end_date')).toBeUndefined();
    } finally {
      db.close();
    }
  });
});
