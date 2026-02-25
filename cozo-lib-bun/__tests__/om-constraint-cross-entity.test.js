const { expect, test, describe } = require('bun:test');

const { createTestDb } = require('./helpers');
const dsl = require('../cozo-dsl');

describe('Phase 2 (track add-action-and-constraints): cross-entity constraints', () => {
  test('defineConstraint stores metadata in om_constraint_def with type cross-entity', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Department', 'Department');
      await om.defineType(db, 'Employee', 'Employee');
      await om.defineRelation(db, 'heads', 'Department', 'Employee', true);

      await om.defineConstraint(db, 'Department', 'at_most_one_head', {
        scope: 'cross-entity',
        when: async () => true,
        then: async (ctx) => {
          const { outgoing } = await ctx.getNeighbors('heads');
          return outgoing.length <= 1;
        },
        message: 'Department can have at most one head',
      });

      const q = dsl.query()
        .select(['constraint_type', 'message'])
        .fromStored('om_constraint_def', {
          type_name: dsl.param('type_name', 'Department'),
          constraint_name: dsl.param('constraint_name', 'at_most_one_head'),
          constraint_type: dsl.var('constraint_type'),
          message: dsl.var('message'),
        })
        .limit(1)
        .build();
      const result = await db.run(q.script, q.params);
      expect(result.rows.length).toBe(1);
      expect(result.rows[0][0]).toBe('cross-entity');
      expect(result.rows[0][1]).toBe('Department can have at most one head');
    } finally {
      db.close();
    }
  });

  test('linkEntities triggers cross-entity constraints unless skipConstraints is true', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Department', 'Department');
      await om.defineType(db, 'Employee', 'Employee');
      await om.defineRelation(db, 'heads', 'Department', 'Employee', true);

      await om.defineConstraint(db, 'Department', 'at_most_one_head', {
        scope: 'cross-entity',
        when: async () => true,
        then: async (ctx) => {
          const { outgoing } = await ctx.getNeighbors('heads');
          return outgoing.length <= 1;
        },
        message: 'Department can have at most one head',
      });

      await om.createEntity(db, 'dept:1', 'Department', 'Dept #1');
      await om.createEntity(db, 'emp:1', 'Employee', 'Alice');
      await om.createEntity(db, 'emp:2', 'Employee', 'Bob');

      await om.linkEntities(db, 'dept:1', 'heads', 'emp:1');
      await expect(om.linkEntities(db, 'dept:1', 'heads', 'emp:2')).rejects.toThrow(
        /at_most_one_head|at most one head/i
      );

      const built = dsl.query()
        .select(['to_id'])
        .fromStored('om_edge', {
          from_id: dsl.param('from_id', 'dept:1'),
          rel_name: dsl.param('rel_name', 'heads'),
          to_id: dsl.var('to_id'),
          props: dsl.var('_props'),
        })
        .build();
      const edges = await db.run(built.script, built.params);
      expect(edges.rows.length).toBe(1);
      expect(edges.rows[0][0]).toBe('emp:1');

      // skipConstraints allows inserting violating state (for bulk load), but validateConstraints should catch it.
      await om.linkEntities(db, 'dept:1', 'heads', 'emp:2', {}, { skipConstraints: true });
      const result = await om.validateConstraints(db, 'dept:1');
      expect(result.valid).toBe(false);
      expect(result.errors.join('\n')).toMatch(/at_most_one_head/);
    } finally {
      db.close();
    }
  });
});
