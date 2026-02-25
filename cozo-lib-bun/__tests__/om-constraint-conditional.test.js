const { expect, test, describe } = require('bun:test');

const { createTestDb } = require('./helpers');
const dsl = require('../cozo-dsl');

describe('Phase 2 (track add-action-and-constraints): conditional constraints', () => {
  test('defineConstraint stores metadata in om_constraint_def', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Employee', 'Employee');
      await om.defineAttribute(db, 'Employee', 'status', 'String', true);
      await om.defineAttribute(db, 'Employee', 'end_date', 'String', false);

      await om.defineConstraint(db, 'Employee', 'active_has_no_end_date', {
        when: async (ctx) => (await ctx.getProperty('status')) === 'active',
        then: async (ctx) => {
          const v = await ctx.getProperty('end_date');
          return v === undefined || v === null || String(v).trim() === '';
        },
        message: 'end_date must be empty when status=active',
      });

      const q = dsl.query()
        .select(['constraint_type', 'message'])
        .fromStored('om_constraint_def', {
          type_name: dsl.param('type_name', 'Employee'),
          constraint_name: dsl.param('constraint_name', 'active_has_no_end_date'),
          constraint_type: dsl.var('constraint_type'),
          message: dsl.var('message'),
        })
        .limit(1)
        .build();
      const result = await db.run(q.script, q.params);
      expect(result.rows.length).toBe(1);
      expect(result.rows[0][0]).toBe('conditional');
      expect(result.rows[0][1]).toBe('end_date must be empty when status=active');
    } finally {
      db.close();
    }
  });

  test('validateConstraints passes for conditional constraint', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Employee', 'Employee');
      await om.defineAttribute(db, 'Employee', 'status', 'String', true);
      await om.defineAttribute(db, 'Employee', 'end_date', 'String', false);

      await om.defineConstraint(db, 'Employee', 'active_has_no_end_date', {
        when: async (ctx) => (await ctx.getProperty('status')) === 'active',
        then: async (ctx) => {
          const v = await ctx.getProperty('end_date');
          return v === undefined || v === null || String(v).trim() === '';
        },
        message: 'end_date must be empty when status=active',
      });

      await om.createEntity(db, 'emp:1', 'Employee', 'Alice');
      await om.setProperty(db, 'emp:1', 'status', 'active');

      const result = await om.validateConstraints(db, 'emp:1');
      expect(result.valid).toBe(true);
      expect(Array.isArray(result.errors)).toBe(true);
      expect(result.errors.length).toBe(0);
    } finally {
      db.close();
    }
  });

  test('validateConstraints fails with constraint name and message', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Employee', 'Employee');
      await om.defineAttribute(db, 'Employee', 'status', 'String', true);
      await om.defineAttribute(db, 'Employee', 'end_date', 'String', false);

      await om.defineConstraint(db, 'Employee', 'active_has_no_end_date', {
        when: async (ctx) => (await ctx.getProperty('status')) === 'active',
        then: async (ctx) => {
          const v = await ctx.getProperty('end_date');
          return v === undefined || v === null || String(v).trim() === '';
        },
        message: 'end_date must be empty when status=active',
      });

      await om.createEntity(db, 'emp:2', 'Employee', 'Bob');
      await om.setProperty(db, 'emp:2', 'status', 'active');
      await om.setProperty(db, 'emp:2', 'end_date', '2026-12-31', { skipConstraints: true });

      const result = await om.validateConstraints(db, 'emp:2');
      expect(result.valid).toBe(false);
      expect(result.errors.length).toBeGreaterThan(0);
      expect(result.errors.join('\n')).toMatch(/active_has_no_end_date/);
      expect(result.errors.join('\n')).toMatch(/end_date must be empty/);
    } finally {
      db.close();
    }
  });

  test('setProperty triggers conditional constraints unless skipConstraints is true', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Employee', 'Employee');
      await om.defineAttribute(db, 'Employee', 'status', 'String', true);
      await om.defineAttribute(db, 'Employee', 'end_date', 'String', false);

      await om.defineConstraint(db, 'Employee', 'active_has_no_end_date', {
        when: async (ctx) => (await ctx.getProperty('status')) === 'active',
        then: async (ctx) => {
          const v = await ctx.getProperty('end_date');
          return v === undefined || v === null || String(v).trim() === '';
        },
        message: 'end_date must be empty when status=active',
      });

      await om.createEntity(db, 'emp:3', 'Employee', 'Carol');
      await om.setProperty(db, 'emp:3', 'status', 'active');

      await expect(
        om.setProperty(db, 'emp:3', 'end_date', '2026-12-31')
      ).rejects.toThrow(/active_has_no_end_date|end_date must be empty/i);

      expect(await om.getProperty(db, 'emp:3', 'end_date')).toBeUndefined();

      await om.setProperty(db, 'emp:3', 'end_date', '2026-12-31', { skipConstraints: true });
      expect(await om.getProperty(db, 'emp:3', 'end_date')).toBe('2026-12-31');
    } finally {
      db.close();
    }
  });
});
