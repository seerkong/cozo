const { expect, test, describe } = require('bun:test');

const { CozoDb } = require('../index');
const { createTestDb } = require('./helpers');
const om = require('../cozo-om');

describe('temporal: om_edge bi-temporal storage', () => {
  test('initSchema creates bi-temporal om_edge with valid_time and tx_time', async () => {
    const db = new CozoDb('mem', '', {});
    try {
      await om.initSchema(db);
      const res = await db.run('::columns om_edge', {});
      const byName = new Map(res.rows.map((r) => [r[0], { isKey: r[1], type: r[3] }]));

      expect(byName.get('from_id')?.isKey).toBe(true);
      expect(byName.get('rel_name')?.isKey).toBe(true);
      expect(byName.get('to_id')?.isKey).toBe(true);

      expect(byName.get('valid_time')?.isKey).toBe(true);
      expect(byName.get('valid_time')?.type).toBe('Validity');

      expect(byName.get('tx_time')?.isKey).toBe(false);
      expect(byName.get('tx_time')?.type).toBe('String');
    } finally {
      db.close();
    }
  });

  test('linkEntities validTime supports future-effective edges filtered by @ NOW', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Employee', 'Employee');
      await om.defineType(db, 'Department', 'Department');
      await om.defineRelation(db, 'belongs_to', 'Employee', 'Department', true);

      await om.createEntity(db, 'emp:1', 'Employee', 'Alice');
      await om.createEntity(db, 'dept:eng', 'Department', 'Engineering');
      await om.createEntity(db, 'dept:future', 'Department', 'FutureDept');

      await om.linkEntities(db, 'emp:1', 'belongs_to', 'dept:eng', {}, { validTime: '2000-01-01T00:00:00Z' });
      await om.linkEntities(db, 'emp:1', 'belongs_to', 'dept:future', {}, { validTime: '2100-01-01T00:00:00Z' });

      const neighbors = await om.getNeighbors(db, 'emp:1', 'belongs_to');
      expect(neighbors.outgoing.map((n) => n.entityId).sort()).toEqual(['dept:eng']);
    } finally {
      db.close();
    }
  });

  test('unlinkEntities retracts an edge effective at validTime', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Employee', 'Employee');
      await om.defineType(db, 'Department', 'Department');
      await om.defineRelation(db, 'belongs_to', 'Employee', 'Department', true);

      await om.createEntity(db, 'emp:2', 'Employee', 'Bob');
      await om.createEntity(db, 'dept:eng2', 'Department', 'Engineering');
      await om.createEntity(db, 'dept:prod2', 'Department', 'Product');

      await om.linkEntities(db, 'emp:2', 'belongs_to', 'dept:eng2', {}, { validTime: '2000-01-01T00:00:00Z' });
      await om.unlinkEntities(db, 'emp:2', 'belongs_to', 'dept:eng2', { validTime: '2001-01-01T00:00:00Z' });
      await om.linkEntities(db, 'emp:2', 'belongs_to', 'dept:prod2', {}, { validTime: '2001-01-01T00:00:00Z' });

      const neighbors = await om.getNeighbors(db, 'emp:2', 'belongs_to');
      expect(neighbors.outgoing.map((n) => n.entityId).sort()).toEqual(['dept:prod2']);
    } finally {
      db.close();
    }
  });
});
