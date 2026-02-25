const { expect, test, describe } = require('bun:test');

const { createTestDb } = require('./helpers');

describe('temporal: getEdgeHistory', () => {
  test('returns full edge history and supports toId + time range filtering', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Employee', 'Employee');
      await om.defineType(db, 'Department', 'Department');
      await om.defineRelation(db, 'belongs_to', 'Employee', 'Department', true);

      await om.createEntity(db, 'emp:1', 'Employee', 'Alice');
      await om.createEntity(db, 'dept:eng', 'Department', 'Engineering');
      await om.createEntity(db, 'dept:prod', 'Department', 'Product');

      await om.linkEntities(db, 'emp:1', 'belongs_to', 'dept:eng', {}, { validTime: '2000-01-01T00:00:00Z' });
      await om.unlinkEntities(db, 'emp:1', 'belongs_to', 'dept:eng', { validTime: '2001-01-01T00:00:00Z' });
      await om.linkEntities(db, 'emp:1', 'belongs_to', 'dept:prod', {}, { validTime: '2001-01-01T00:00:00Z' });

      const all = await om.getEdgeHistory(db, 'emp:1', 'belongs_to');
      expect(all.length).toBe(3);
      expect(all.map((e) => e.toId)).toEqual(['dept:eng', 'dept:eng', 'dept:prod']);
      expect(all.map((e) => e.is_assert)).toEqual([true, false, true]);

      const onlyEng = await om.getEdgeHistory(db, 'emp:1', 'belongs_to', 'dept:eng');
      expect(onlyEng.length).toBe(2);
      expect(onlyEng.every((e) => e.toId === 'dept:eng')).toBe(true);

      const in2000 = await om.getEdgeHistory(db, 'emp:1', 'belongs_to', undefined, {
        from: '2000-01-01T00:00:00Z',
        to: '2000-12-31T23:59:59Z',
      });
      expect(in2000.length).toBe(1);
      expect(in2000[0].toId).toBe('dept:eng');
      expect(in2000[0].is_assert).toBe(true);
    } finally {
      db.close();
    }
  });
});
