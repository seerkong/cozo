const { expect, test, describe } = require('bun:test');

const { createTestDb } = require('./helpers');

describe('Phase 3 (track add-temporal-dimension): ActionContext temporal integration', () => {
  test('ctx.setProperty forwards validTime options', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Resource', 'Resource');
      await om.defineAttribute(db, 'Resource', 'status', 'String', false);
      await om.createEntity(db, 'resource:1', 'Resource', 'Resource #1');

      await om.defineAction(db, 'Resource', 'approve_at_2000', async () => [
        { mutation: 'setStatusAt', params: { status: 'approved', validTime: '2000-01-01T00:00:00Z' } },
      ]);

      await om.defineMutation(db, 'Resource', 'setStatusAt', async (ctx, params) => {
        await ctx.setProperty('status', String(params.status), { validTime: String(params.validTime) });
      });

      await om.executeAction(db, 'resource:1', 'approve_at_2000', {});

      const before = await om.getPropertyAsOf(db, 'resource:1', 'status', '1999-06-01T00:00:00Z');
      expect(before).toBeUndefined();
      const after = await om.getPropertyAsOf(db, 'resource:1', 'status', '2000-06-01T00:00:00Z');
      expect(after).toBe('approved');
    } finally {
      db.close();
    }
  });

  test('ctx.getProperty supports { asOf }', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Resource', 'Resource');
      await om.defineAttribute(db, 'Resource', 'status', 'String', false);
      await om.defineAttribute(db, 'Resource', 'status_at_2000', 'String', false);
      await om.createEntity(db, 'resource:2', 'Resource', 'Resource #2');

      await om.setProperty(db, 'resource:2', 'status', 'pending', { validTime: '1999-01-01T00:00:00Z' });
      await om.setProperty(db, 'resource:2', 'status', 'approved', { validTime: '2001-01-01T00:00:00Z' });

      await om.defineAction(db, 'Resource', 'snapshot_status', async () => [
        { mutation: 'copyStatusAt', params: { asOf: '2000-06-01T00:00:00Z' } },
      ]);
      await om.defineMutation(db, 'Resource', 'copyStatusAt', async (ctx, params) => {
        const v = await ctx.getProperty('status', { asOf: String(params.asOf) });
        await ctx.setProperty('status_at_2000', v);
      });

      await om.executeAction(db, 'resource:2', 'snapshot_status', {});
      expect(await om.getProperty(db, 'resource:2', 'status_at_2000')).toBe('pending');
    } finally {
      db.close();
    }
  });

  test('constraint validation in actions uses latest effective values (@ NOW)', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Resource', 'Resource');
      await om.defineAttribute(db, 'Resource', 'status', 'String', false);
      await om.defineAttribute(db, 'Resource', 'end_date', 'String', false);
      await om.createEntity(db, 'resource:3', 'Resource', 'Resource #3');
      await om.setProperty(db, 'resource:3', 'status', 'pending');

      await om.defineConstraint(db, 'Resource', 'active_requires_no_end_date', {
        when: async (ctx) => (await ctx.getProperty('status')) === 'active',
        then: async (ctx) => (await ctx.getProperty('end_date')) == null,
        message: 'end_date must be empty when status is active',
      });

      await om.defineAction(db, 'Resource', 'invalidate', async () => [
        { mutation: 'setStatus', params: { status: 'active' } },
        { mutation: 'setEndDate', params: { end_date: '2026-12-31' } },
      ]);
      await om.defineMutation(db, 'Resource', 'setStatus', async (ctx, params) => {
        await ctx.setProperty('status', String(params.status));
      });
      await om.defineMutation(db, 'Resource', 'setEndDate', async (ctx, params) => {
        await ctx.setProperty('end_date', String(params.end_date));
      });

      await expect(om.executeAction(db, 'resource:3', 'invalidate', {})).rejects.toThrow(
        /end_date must be empty|active_requires_no_end_date/i
      );
      expect(await om.getProperty(db, 'resource:3', 'status')).toBe('pending');
      expect(await om.getProperty(db, 'resource:3', 'end_date')).toBeUndefined();
    } finally {
      db.close();
    }
  });
});
