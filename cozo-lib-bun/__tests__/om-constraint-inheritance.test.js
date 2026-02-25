const { expect, test, describe } = require('bun:test');

const { createTestDb } = require('./helpers');

describe('Phase 2 (track add-action-and-constraints): constraint inheritance', () => {
  test('subtype inherits conditional constraint and setProperty triggers it', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Asset', 'Asset');
      await om.defineType(db, 'ITAsset', 'ITAsset', { parentType: 'Asset' });
      await om.defineAttribute(db, 'Asset', 'status', 'String', true);
      await om.defineAttribute(db, 'Asset', 'end_date', 'String', false);

      await om.defineConstraint(db, 'Asset', 'active_has_no_end_date', {
        when: async (ctx) => (await ctx.getProperty('status')) === 'active',
        then: async (ctx) => {
          const v = await ctx.getProperty('end_date');
          return v === undefined || v === null || String(v).trim() === '';
        },
        message: 'end_date must be empty when status=active',
      });

      await om.createEntity(db, 'it:1', 'ITAsset', 'ITAsset #1');
      await om.setProperty(db, 'it:1', 'status', 'active');

      await expect(om.setProperty(db, 'it:1', 'end_date', '2026-12-31')).rejects.toThrow(
        /active_has_no_end_date|end_date must be empty/i
      );
      expect(await om.getProperty(db, 'it:1', 'end_date')).toBeUndefined();
    } finally {
      db.close();
    }
  });

  test('subtype inherits cross-entity constraint and linkEntities triggers it', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Department', 'Department');
      await om.defineType(db, 'SpecialDepartment', 'SpecialDepartment', { parentType: 'Department' });
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

      await om.createEntity(db, 'dept:1', 'SpecialDepartment', 'Dept #1');
      await om.createEntity(db, 'emp:1', 'Employee', 'Alice');
      await om.createEntity(db, 'emp:2', 'Employee', 'Bob');

      await om.linkEntities(db, 'dept:1', 'heads', 'emp:1');
      await expect(om.linkEntities(db, 'dept:1', 'heads', 'emp:2')).rejects.toThrow(
        /at_most_one_head|at most one head/i
      );
    } finally {
      db.close();
    }
  });
});
