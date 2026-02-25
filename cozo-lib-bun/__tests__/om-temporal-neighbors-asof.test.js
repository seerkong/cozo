const { expect, test, describe } = require('bun:test');

const { createTestDb } = require('./helpers');

describe('temporal: getNeighborsAsOf', () => {
  test('returns outgoing neighbors effective at asOf (RETRACT filtered)', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Employee', 'Employee');
      await om.defineType(db, 'Department', 'Department');
      await om.defineRelation(db, 'belongs_to', 'Employee', 'Department', true);

      await om.createEntity(db, 'emp:1', 'Employee', 'Alice');
      await om.createEntity(db, 'dept:eng', 'Department', 'Engineering');

      await om.linkEntities(db, 'emp:1', 'belongs_to', 'dept:eng', {}, { validTime: '2000-01-01T00:00:00Z' });
      await om.unlinkEntities(db, 'emp:1', 'belongs_to', 'dept:eng', { validTime: '2001-01-01T00:00:00Z' });

      const before = await om.getNeighborsAsOf(db, 'emp:1', 'belongs_to', '2000-06-01T00:00:00Z');
      expect(before.outgoing.map((n) => n.entityId).sort()).toEqual(['dept:eng']);

      const after = await om.getNeighborsAsOf(db, 'emp:1', 'belongs_to', '2001-06-01T00:00:00Z');
      expect(after.outgoing.map((n) => n.entityId).sort()).toEqual([]);
    } finally {
      db.close();
    }
  });
});
