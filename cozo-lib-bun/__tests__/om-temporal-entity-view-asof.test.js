const { expect, test, describe } = require('bun:test');

const { createTestDb } = require('./helpers');

describe('temporal: getEntityViewAsOf', () => {
  test('returns entity view with properties effective at asOf', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Employee', 'Employee');
      await om.defineAttribute(db, 'Employee', 'department', 'String', false);
      await om.defineAttribute(db, 'Employee', 'role', 'String', false);
      await om.createEntity(db, 'emp:1', 'Employee', 'Alice');

      await om.setProperty(db, 'emp:1', 'department', 'Engineering', {
        validTime: '2025-01-01T00:00:00Z',
      });
      await om.setProperty(db, 'emp:1', 'department', 'Product', {
        validTime: '2026-01-01T00:00:00Z',
      });

      await om.setProperty(db, 'emp:1', 'role', 'IC', {
        validTime: '2025-01-01T00:00:00Z',
      });

      const view = await om.getEntityViewAsOf(db, 'emp:1', '2025-06-01T00:00:00Z');
      expect(view).toBeTruthy();
      expect(view.id).toBe('emp:1');
      expect(view.typeName).toBe('Employee');
      expect(view.properties.department).toBe('Engineering');
      expect(view.properties.role).toBe('IC');
    } finally {
      db.close();
    }
  });

  test('includes computed properties evaluated against asOf context', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Employee', 'Employee');
      await om.defineAttribute(db, 'Employee', 'department', 'String', false);
      await om.createEntity(db, 'emp:2', 'Employee', 'Bob');

      await om.setProperty(db, 'emp:2', 'department', 'Engineering', {
        validTime: '2025-01-01T00:00:00Z',
      });
      await om.setProperty(db, 'emp:2', 'department', 'Product', {
        validTime: '2026-01-01T00:00:00Z',
      });

      await om.defineComputed(db, 'Employee', 'dept_code', async (ctx) => {
        const dept = await ctx.getProperty('department');
        return String(dept || '').slice(0, 3).toUpperCase();
      });

      const view = await om.getEntityViewAsOf(db, 'emp:2', '2025-06-01T00:00:00Z');
      expect(view.properties.dept_code).toBe('ENG');
    } finally {
      db.close();
    }
  });
});
