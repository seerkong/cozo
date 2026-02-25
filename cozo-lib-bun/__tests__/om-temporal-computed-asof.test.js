const { expect, test, describe } = require('bun:test');

const { createTestDb } = require('./helpers');

describe('Phase 3 (track add-temporal-dimension): computed asOf context', () => {
  test('getPropertyAsOf evaluates computed properties against asOf snapshot', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Asset', 'Asset');
      await om.defineAttribute(db, 'Asset', 'base_risk', 'Number', true);
      await om.createEntity(db, 'asset:1', 'Asset', 'Asset #1');

      await om.setProperty(db, 'asset:1', 'base_risk', 10, { validTime: '1999-01-01T00:00:00Z' });
      await om.setProperty(db, 'asset:1', 'base_risk', 20, { validTime: '2001-01-01T00:00:00Z' });

      await om.defineComputed(db, 'Asset', 'risk_score', async (ctx) => {
        // asOf should be provided when invoked from getPropertyAsOf
        expect(typeof ctx.asOf).toBe('string');
        expect(Number.isFinite(Date.parse(ctx.asOf))).toBe(true);
        const base = await ctx.getProperty('base_risk');
        return Number(base) * 10;
      });

      const v2000 = await om.getPropertyAsOf(db, 'asset:1', 'risk_score', '2000-06-01T00:00:00Z');
      expect(v2000).toBe(100);

      const v2002 = await om.getPropertyAsOf(db, 'asset:1', 'risk_score', '2002-06-01T00:00:00Z');
      expect(v2002).toBe(200);
    } finally {
      db.close();
    }
  });
});
