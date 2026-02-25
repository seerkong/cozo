const { expect, test, describe } = require('bun:test');

const { createTestDb } = require('./helpers');

describe('Phase 2 (track add-action-and-constraints): interceptors', () => {
  test('before interceptor blocks action execution', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Asset', 'Asset');
      await om.defineAttribute(db, 'Asset', 'status', 'String', true);

      await om.createEntity(db, 'asset:1', 'Asset', 'Asset #1');
      await om.setProperty(db, 'asset:1', 'status', 'approved');

      await om.defineMutation(db, 'Asset', 'setStatus', async (ctx, params) => {
        await ctx.setProperty('status', String(params.status));
      });

      await om.defineAction(db, 'Asset', 'approve', async () => [
        { mutation: 'setStatus', params: { status: 'approved' } },
      ]);

      await om.addInterceptor(db, 'Asset', 'approve', 'before', async (ctx) => {
        const status = await ctx.getProperty('status');
        if (status !== 'pending') {
          throw new Error('must be pending');
        }
      });

      await expect(om.executeAction(db, 'asset:1', 'approve', {})).rejects.toThrow('must be pending');
      expect(await om.getProperty(db, 'asset:1', 'status')).toBe('approved');
    } finally {
      db.close();
    }
  });

  test('after interceptor runs after mutations', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Asset', 'Asset');
      await om.defineAttribute(db, 'Asset', 'status', 'String', true);

      await om.createEntity(db, 'asset:2', 'Asset', 'Asset #2');
      await om.setProperty(db, 'asset:2', 'status', 'pending');

      await om.defineMutation(db, 'Asset', 'setStatus', async (ctx, params) => {
        await ctx.setProperty('status', String(params.status));
      });

      await om.defineAction(db, 'Asset', 'approve', async () => [
        { mutation: 'setStatus', params: { status: 'approved' } },
      ]);

      let called = false;
      await om.addInterceptor(db, 'Asset', 'approve', 'after', async (ctx) => {
        const status = await ctx.getProperty('status');
        expect(status).toBe('approved');
        called = true;
      });

      await om.executeAction(db, 'asset:2', 'approve', {});
      expect(called).toBe(true);
    } finally {
      db.close();
    }
  });

  test('multiple interceptors run in registration order', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Asset', 'Asset');
      await om.createEntity(db, 'asset:3', 'Asset', 'Asset #3');

      const events = [];

      await om.defineAction(db, 'Asset', 'noop', async () => {
        events.push('action');
        return [];
      });

      await om.addInterceptor(db, 'Asset', 'noop', 'before', async () => {
        events.push('before-1');
      });
      await om.addInterceptor(db, 'Asset', 'noop', 'before', async () => {
        events.push('before-2');
      });
      await om.addInterceptor(db, 'Asset', 'noop', 'after', async () => {
        events.push('after-1');
      });

      await om.executeAction(db, 'asset:3', 'noop', {});
      expect(events).toEqual(['before-1', 'before-2', 'action', 'after-1']);
    } finally {
      db.close();
    }
  });

  test('interceptor inheritance: parent interceptor applies to subtype', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Asset', 'Asset');
      await om.defineType(db, 'ITAsset', 'ITAsset', { parentType: 'Asset' });
      await om.defineAttribute(db, 'Asset', 'status', 'String', true);

      await om.defineAction(db, 'Asset', 'approve', async () => []);

      await om.addInterceptor(db, 'Asset', 'approve', 'before', async () => {
        throw new Error('blocked-by-parent');
      });

      await om.createEntity(db, 'it:1', 'ITAsset', 'ITAsset #1');
      await om.setProperty(db, 'it:1', 'status', 'pending');

      await expect(om.executeAction(db, 'it:1', 'approve', {})).rejects.toThrow('blocked-by-parent');
    } finally {
      db.close();
    }
  });
});
