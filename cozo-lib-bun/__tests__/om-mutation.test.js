const { expect, test, describe } = require('bun:test');

const { createTestDb } = require('./helpers');
const dsl = require('../cozo-dsl');

describe('Phase 2 (track add-action-and-constraints): mutations', () => {
  test('defineMutation stores metadata in om_mutation_def', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Asset', 'Asset');
      await om.defineAttribute(db, 'Asset', 'status', 'String', true);

      await om.defineMutation(
        db,
        'Asset',
        'setStatus',
        async (ctx, params) => {
          await ctx.setProperty('status', String(params.status));
        },
        'Set status'
      );

      const q = dsl.query()
        .select(['description'])
        .fromStored('om_mutation_def', {
          type_name: dsl.param('type_name', 'Asset'),
          mutation_name: dsl.param('mutation_name', 'setStatus'),
          description: dsl.var('description'),
        })
        .limit(1)
        .build();

      const result = await db.run(q.script, q.params);
      expect(result.rows.length).toBe(1);
      expect(result.rows[0][0]).toBe('Set status');
    } finally {
      db.close();
    }
  });

  test('executeMutations applies mutations in a single transaction', async () => {
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
        },
        'Set status'
      );

      await om.executeMutations(db, 'asset:1', [
        { mutation: 'setStatus', params: { status: 'approved' } },
      ]);

      const status = await om.getProperty(db, 'asset:1', 'status');
      expect(status).toBe('approved');
    } finally {
      db.close();
    }
  });

  test('executeMutations rolls back if a later mutation fails', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Asset', 'Asset');
      await om.defineAttribute(db, 'Asset', 'status', 'String', true);
      await om.createEntity(db, 'asset:2', 'Asset', 'Asset #2');
      await om.setProperty(db, 'asset:2', 'status', 'pending');

      await om.defineMutation(
        db,
        'Asset',
        'setStatus',
        async (ctx, params) => {
          await ctx.setProperty('status', String(params.status));
        }
      );
      await om.defineMutation(
        db,
        'Asset',
        'fail',
        async () => {
          throw new Error('boom');
        }
      );

      await expect(
        om.executeMutations(db, 'asset:2', [
          { mutation: 'setStatus', params: { status: 'approved' } },
          { mutation: 'fail', params: {} },
        ])
      ).rejects.toThrow('boom');

      const status = await om.getProperty(db, 'asset:2', 'status');
      expect(status).toBe('pending');
    } finally {
      db.close();
    }
  });
});
