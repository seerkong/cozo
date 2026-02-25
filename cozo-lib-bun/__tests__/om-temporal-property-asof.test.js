const { expect, test, describe } = require('bun:test');

const { createTestDb } = require('./helpers');

describe('temporal: getPropertyAsOf', () => {
  test('returns effective value at a historical timestamp', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Employee', 'Employee');
      await om.defineAttribute(db, 'Employee', 'department', 'String', false);
      await om.createEntity(db, 'emp:1', 'Employee', 'Alice');

      await om.setProperty(db, 'emp:1', 'department', 'Engineering', {
        validTime: '2025-01-01T00:00:00Z',
      });
      await om.setProperty(db, 'emp:1', 'department', 'Product', {
        validTime: '2026-01-01T00:00:00Z',
      });

      const v = await om.getPropertyAsOf(db, 'emp:1', 'department', '2025-06-01T00:00:00Z');
      expect(v).toBe('Engineering');
    } finally {
      db.close();
    }
  });

  test('returns latest known value when querying a future timestamp', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Employee', 'Employee');
      await om.defineAttribute(db, 'Employee', 'department', 'String', false);
      await om.createEntity(db, 'emp:2', 'Employee', 'Bob');

      await om.setProperty(db, 'emp:2', 'department', 'Eng', {
        validTime: '2025-01-01T00:00:00Z',
      });
      await om.setProperty(db, 'emp:2', 'department', 'Product', {
        validTime: '2026-01-01T00:00:00Z',
      });

      const v = await om.getPropertyAsOf(db, 'emp:2', 'department', '2030-01-01T00:00:00Z');
      expect(v).toBe('Product');
    } finally {
      db.close();
    }
  });

  test('returns undefined when no version exists at/before the timestamp', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Employee', 'Employee');
      await om.defineAttribute(db, 'Employee', 'department', 'String', false);
      await om.createEntity(db, 'emp:3', 'Employee', 'Carol');

      await om.setProperty(db, 'emp:3', 'department', 'Eng', {
        validTime: '2025-01-01T00:00:00Z',
      });

      const v = await om.getPropertyAsOf(db, 'emp:3', 'department', '2024-01-01T00:00:00Z');
      expect(v).toBeUndefined();
    } finally {
      db.close();
    }
  });
});
