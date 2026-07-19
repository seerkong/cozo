const { expect, test, describe } = require('bun:test');

const { createTestDb } = require('./helpers');
const dsl = require('../cozo-dsl');

describe('Phase 2 (track add-action-and-constraints): computed properties', () => {
  test('defineComputed stores metadata in om_computed_def', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Resource', 'Resource');
      await om.defineComputed(db, 'Resource', 'risk_score', async () => 42, 'Risk score');

      const q = dsl.query()
        .select(['description'])
        .fromStored('om_computed_def', {
          type_name: dsl.param('type_name', 'Resource'),
          attr_name: dsl.param('attr_name', 'risk_score'),
          description: dsl.var('description'),
        })
        .limit(1)
        .build();
      const result = await db.run(q.script, q.params);
      expect(result.rows.length).toBe(1);
      expect(result.rows[0][0]).toBe('Risk score');
    } finally {
      db.close();
    }
  });

  test('getProperty returns computed value when not stored', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Resource', 'Resource');
      await om.defineAttribute(db, 'Resource', 'base_risk', 'Number', true);
      await om.createEntity(db, 'resource:1', 'Resource', 'Resource #1');
      await om.setProperty(db, 'resource:1', 'base_risk', 10);

      await om.defineComputed(db, 'Resource', 'risk_score', async (ctx) => {
        const base = await ctx.getProperty('base_risk');
        return Number(base) * 10;
      });

      const v = await om.getProperty(db, 'resource:1', 'risk_score');
      expect(v).toBe(100);
    } finally {
      db.close();
    }
  });

  test('getEntityView includes computed properties', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Resource', 'Resource');
      await om.defineAttribute(db, 'Resource', 'base_risk', 'Number', true);
      await om.createEntity(db, 'resource:2', 'Resource', 'Resource #2');
      await om.setProperty(db, 'resource:2', 'base_risk', 7);

      await om.defineComputed(db, 'Resource', 'risk_score', async (ctx) => {
        return Number(await ctx.getProperty('base_risk')) * 2;
      });

      const view = await om.getEntityView(db, 'resource:2');
      expect(view).toBeTruthy();
      expect(view.properties.risk_score).toBe(14);
    } finally {
      db.close();
    }
  });

  test('setProperty rejects writing computed properties', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Resource', 'Resource');
      await om.defineAttribute(db, 'Resource', 'base_risk', 'Number', true);
      await om.createEntity(db, 'resource:3', 'Resource', 'Resource #3');
      await om.setProperty(db, 'resource:3', 'base_risk', 1);

      await om.defineComputed(db, 'Resource', 'risk_score', async () => 5);
      await expect(om.setProperty(db, 'resource:3', 'risk_score', 999)).rejects.toThrow(
        /computed property|Cannot set/i
      );
    } finally {
      db.close();
    }
  });

  test('computed property can be used in constraints', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Resource', 'Resource');
      await om.defineAttribute(db, 'Resource', 'base_risk', 'Number', true);
      await om.defineAttribute(db, 'Resource', 'requires_review', 'Bool', false);

      await om.createEntity(db, 'resource:4', 'Resource', 'Resource #4');
      await om.setProperty(db, 'resource:4', 'base_risk', 9);

      await om.defineComputed(db, 'Resource', 'risk_score', async (ctx) => {
        return Number(await ctx.getProperty('base_risk')) * 10;
      });

      await om.defineConstraint(db, 'Resource', 'high_risk_requires_review', {
        when: async (ctx) => Number(await ctx.getProperty('risk_score')) > 80,
        then: async (ctx) => (await ctx.getProperty('requires_review')) === true,
        message: 'requires_review must be true when risk_score > 80',
      });

      const result = await om.validateConstraints(db, 'resource:4');
      expect(result.valid).toBe(false);
      expect(result.errors.join('\n')).toMatch(/high_risk_requires_review/);
    } finally {
      db.close();
    }
  });

  test('computed property inheritance: subtype override takes precedence', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Resource', 'Resource');
      await om.defineType(db, 'ExecutableResource', 'ExecutableResource', { parentType: 'Resource' });

      await om.defineComputed(db, 'Resource', 'risk_score', async () => 1);
      await om.defineComputed(db, 'ExecutableResource', 'risk_score', async () => 2);

      await om.createEntity(db, 'exec:1', 'ExecutableResource', 'ExecutableResource #1');
      const v = await om.getProperty(db, 'exec:1', 'risk_score');
      expect(v).toBe(2);
    } finally {
      db.close();
    }
  });
});
