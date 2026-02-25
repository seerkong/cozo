const { expect, test, describe } = require('bun:test');

const { createTestDb } = require('./helpers');

describe('temporal: getPropertyHistory', () => {
  test('returns full property history sorted by valid_time', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Employee', 'Employee');
      await om.defineAttribute(db, 'Employee', 'department', 'String', false);
      await om.createEntity(db, 'emp:1', 'Employee', 'Alice');

      await om.setProperty(db, 'emp:1', 'department', 'Engineering', {
        validTime: '2025-01-01T00:00:00Z',
      });
      await om.setProperty(db, 'emp:1', 'department', 'Product', {
        validTime: '2025-06-01T00:00:00Z',
      });
      await om.setProperty(db, 'emp:1', 'department', 'Management', {
        validTime: '2026-01-01T00:00:00Z',
      });

      const history = await om.getPropertyHistory(db, 'emp:1', 'department');
      expect(Array.isArray(history)).toBe(true);
      expect(history.length).toBe(3);

      expect(history.map((h) => h.value)).toEqual(['Engineering', 'Product', 'Management']);

      const ts = history.map((h) => Date.parse(h.valid_time));
      expect(ts.every((x) => Number.isFinite(x))).toBe(true);
      expect(ts[0]).toBeLessThan(ts[1]);
      expect(ts[1]).toBeLessThan(ts[2]);

      expect(typeof history[0].tx_time).toBe('string');
      expect(Number.isFinite(Date.parse(history[0].tx_time))).toBe(true);
    } finally {
      db.close();
    }
  });

  test('supports from/to range filtering by valid_time', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Employee', 'Employee');
      await om.defineAttribute(db, 'Employee', 'department', 'String', false);
      await om.createEntity(db, 'emp:2', 'Employee', 'Bob');

      await om.setProperty(db, 'emp:2', 'department', 'A', { validTime: '2025-01-01T00:00:00Z' });
      await om.setProperty(db, 'emp:2', 'department', 'B', { validTime: '2025-06-01T00:00:00Z' });
      await om.setProperty(db, 'emp:2', 'department', 'C', { validTime: '2026-01-01T00:00:00Z' });

      const history = await om.getPropertyHistory(db, 'emp:2', 'department', {
        from: '2025-05-01T00:00:00Z',
        to: '2025-12-31T23:59:59Z',
      });
      expect(history.map((h) => h.value)).toEqual(['B']);
    } finally {
      db.close();
    }
  });
});
