const { expect, test, describe } = require('bun:test');

const { createTestDb } = require('./helpers');

async function createParentChildActionFixture(entityId, onAction) {
  const { db, om } = await createTestDb();
  await om.defineType(db, 'Parent', 'Parent');
  await om.defineType(db, 'Child', 'Child', { parentType: 'Parent' });
  await om.defineAttribute(db, 'Parent', 'status', 'String', false);
  await om.defineAttribute(db, 'Parent', 'interceptor_note', 'String', false);
  await om.createEntity(db, entityId, 'Child', entityId);
  await om.setProperty(db, entityId, 'status', 'initial');
  await om.setProperty(db, entityId, 'interceptor_note', 'initial');

  const calls = { action: 0, mutation: 0 };
  await om.defineMutation(db, 'Parent', 'applyChange', async (ctx) => {
    calls.mutation += 1;
    await ctx.setProperty('status', 'mutated');
  });
  await om.defineAction(db, 'Child', 'change', async () => {
    calls.action += 1;
    if (onAction) onAction();
    return [{ mutation: 'applyChange', params: {} }];
  });

  return { db, om, calls };
}

describe('Phase 2 (track add-action-and-constraints): interceptors', () => {
  test('before interceptor blocks action execution', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Resource', 'Resource');
      await om.defineAttribute(db, 'Resource', 'status', 'String', true);

      await om.createEntity(db, 'resource:1', 'Resource', 'Resource #1');
      await om.setProperty(db, 'resource:1', 'status', 'approved');

      await om.defineMutation(db, 'Resource', 'setStatus', async (ctx, params) => {
        await ctx.setProperty('status', String(params.status));
      });

      await om.defineAction(db, 'Resource', 'approve', async () => [
        { mutation: 'setStatus', params: { status: 'approved' } },
      ]);

      await om.addInterceptor(db, 'Resource', 'approve', 'before', async (ctx) => {
        const status = await ctx.getProperty('status');
        if (status !== 'pending') {
          throw new Error('must be pending');
        }
      });

      await expect(om.executeAction(db, 'resource:1', 'approve', {})).rejects.toThrow('must be pending');
      expect(await om.getProperty(db, 'resource:1', 'status')).toBe('approved');
    } finally {
      db.close();
    }
  });

  test('after interceptor runs after mutations', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Resource', 'Resource');
      await om.defineAttribute(db, 'Resource', 'status', 'String', true);

      await om.createEntity(db, 'resource:2', 'Resource', 'Resource #2');
      await om.setProperty(db, 'resource:2', 'status', 'pending');

      await om.defineMutation(db, 'Resource', 'setStatus', async (ctx, params) => {
        await ctx.setProperty('status', String(params.status));
      });

      await om.defineAction(db, 'Resource', 'approve', async () => [
        { mutation: 'setStatus', params: { status: 'approved' } },
      ]);

      let called = false;
      await om.addInterceptor(db, 'Resource', 'approve', 'after', async (ctx) => {
        const status = await ctx.getProperty('status');
        expect(status).toBe('approved');
        called = true;
      });

      await om.executeAction(db, 'resource:2', 'approve', {});
      expect(called).toBe(true);
    } finally {
      db.close();
    }
  });

  test('multiple interceptors run in registration order', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Resource', 'Resource');
      await om.createEntity(db, 'resource:3', 'Resource', 'Resource #3');

      const events = [];

      await om.defineAction(db, 'Resource', 'noop', async () => {
        events.push('action');
        return [];
      });

      await om.addInterceptor(db, 'Resource', 'noop', 'before', async () => {
        events.push('before-1');
      });
      await om.addInterceptor(db, 'Resource', 'noop', 'before', async () => {
        events.push('before-2');
      });
      await om.addInterceptor(db, 'Resource', 'noop', 'after', async () => {
        events.push('after-1');
      });

      await om.executeAction(db, 'resource:3', 'noop', {});
      expect(events).toEqual(['before-1', 'before-2', 'action', 'after-1']);
    } finally {
      db.close();
    }
  });

  test('interceptor inheritance: parent interceptor applies to subtype', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Resource', 'Resource');
      await om.defineType(db, 'ExecutableResource', 'ExecutableResource', { parentType: 'Resource' });
      await om.defineAttribute(db, 'Resource', 'status', 'String', true);

      await om.defineAction(db, 'Resource', 'approve', async () => []);

      await om.addInterceptor(db, 'Resource', 'approve', 'before', async () => {
        throw new Error('blocked-by-parent');
      });

      await om.createEntity(db, 'exec:1', 'ExecutableResource', 'ExecutableResource #1');
      await om.setProperty(db, 'exec:1', 'status', 'pending');

      await expect(om.executeAction(db, 'exec:1', 'approve', {})).rejects.toThrow('blocked-by-parent');
    } finally {
      db.close();
    }
  });

  test('parent and child interceptors preserve owner-local order in both phases', async () => {
    const events = [];
    const { db, om } = await createParentChildActionFixture('child:order', () => {
      events.push('action');
    });
    try {
      for (const owner of ['Parent', 'Child']) {
        for (const phase of ['before', 'after']) {
          for (let index = 1; index <= 2; index += 1) {
            await om.addInterceptor(db, owner, 'change', phase, async () => {
              events.push(`${owner.toLowerCase()}-${phase}${index}`);
            });
          }
        }
      }

      await om.executeAction(db, 'child:order', 'change', {});

      expect(events).toEqual([
        'parent-before1',
        'parent-before2',
        'child-before1',
        'child-before2',
        'action',
        'parent-after1',
        'parent-after2',
        'child-after1',
        'child-after2',
      ]);
    } finally {
      db.close();
    }
  });

  test('inherited parent before failure skips action and mutation and rolls back interceptor writes', async () => {
    const { db, om, calls } = await createParentChildActionFixture('child:before-failure');
    try {
      await om.addInterceptor(db, 'Parent', 'change', 'before', async (ctx) => {
        await ctx.setProperty('interceptor_note', 'before-write');
      });
      await om.addInterceptor(db, 'Parent', 'change', 'before', async () => {
        throw new Error('parent before failed');
      });

      await expect(om.executeAction(db, 'child:before-failure', 'change', {})).rejects.toThrow(
        'parent before failed'
      );

      expect(calls).toEqual({ action: 0, mutation: 0 });
      expect(await om.getProperty(db, 'child:before-failure', 'status')).toBe('initial');
      expect(await om.getProperty(db, 'child:before-failure', 'interceptor_note')).toBe('initial');
    } finally {
      db.close();
    }
  });

  test('inherited parent after failure rolls back mutation and interceptor writes', async () => {
    const { db, om, calls } = await createParentChildActionFixture('child:after-failure');
    try {
      await om.addInterceptor(db, 'Parent', 'change', 'after', async (ctx) => {
        await ctx.setProperty('interceptor_note', 'after-write');
        throw new Error('parent after failed');
      });

      await expect(om.executeAction(db, 'child:after-failure', 'change', {})).rejects.toThrow(
        'parent after failed'
      );

      expect(calls).toEqual({ action: 1, mutation: 1 });
      expect(await om.getProperty(db, 'child:after-failure', 'status')).toBe('initial');
      expect(await om.getProperty(db, 'child:after-failure', 'interceptor_note')).toBe('initial');
    } finally {
      db.close();
    }
  });
});
