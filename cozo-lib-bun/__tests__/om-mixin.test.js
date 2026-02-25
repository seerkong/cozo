const { describe, expect, test } = require('bun:test');
const { createTestDb } = require('./helpers');

describe('defineMixin', () => {
  test('defineMixin creates a mixin that can be queried', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineMixin(db, 'Auditable', 'Tracks audit fields');

      // Verify mixin exists by using it in defineType without error
      await om.defineType(db, 'Asset', 'Asset', { mixins: ['Auditable'] });
    } finally {
      db.close();
    }
  });

  test('defineMixin can define multiple mixins', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineMixin(db, 'Auditable', 'Tracks audit fields');
      await om.defineMixin(db, 'Taggable', 'Supports tags');

      await om.defineType(db, 'Asset', 'Asset', { mixins: ['Auditable', 'Taggable'] });
      // No error means both mixins were found
    } finally {
      db.close();
    }
  });
});

describe('defineType with mixins', () => {
  test('defineType with valid mixins works', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineMixin(db, 'Auditable', 'Tracks audit fields');
      await om.defineType(db, 'Asset', 'Asset', { mixins: ['Auditable'] });

      // Verify the type was created
      const hierarchy = await om.getTypeHierarchy(db);
      const typeNames = Object.keys(hierarchy.types);
      expect(typeNames).toContain('Asset');
    } finally {
      db.close();
    }
  });

  test('defineType with unknown mixin throws', async () => {
    const { db, om } = await createTestDb();
    try {
      await expect(
        om.defineType(db, 'Asset', 'Asset', { mixins: ['NonExistentMixin'] })
      ).rejects.toThrow("Mixin 'NonExistentMixin' does not exist");
    } finally {
      db.close();
    }
  });

  test('defineType with mix of valid and unknown mixins throws on the unknown one', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineMixin(db, 'Auditable', 'Tracks audit fields');

      await expect(
        om.defineType(db, 'Asset', 'Asset', { mixins: ['Auditable', 'Ghost'] })
      ).rejects.toThrow("Mixin 'Ghost' does not exist");
    } finally {
      db.close();
    }
  });

  test('defineType with parentType and mixins together works', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineMixin(db, 'Auditable', 'Tracks audit fields');
      await om.defineType(db, 'Asset', 'Asset');
      await om.defineType(db, 'Server', 'Server', {
        parentType: 'Asset',
        mixins: ['Auditable'],
      });

      const ancestors = await om.getAncestors(db, 'Server');
      expect(ancestors).toEqual(['Asset']);
    } finally {
      db.close();
    }
  });

  test('mixin attributes are inherited by type', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineMixin(db, 'Auditable', 'Tracks audit fields');
      await om.defineAttribute(db, 'Auditable', 'created_by', 'String', false);

      await om.defineType(db, 'Asset', 'Asset', { mixins: ['Auditable'] });
      await om.defineAttribute(db, 'Asset', 'name', 'String', true);

      await om.createEntity(db, 'a:1', 'Asset', 'My Asset');
      await om.setProperty(db, 'a:1', 'name', 'Test');
      // Should be able to set mixin-inherited attribute
      await om.setProperty(db, 'a:1', 'created_by', 'admin');

      const val = await om.getProperty(db, 'a:1', 'created_by');
      expect(val).toBe('admin');
    } finally {
      db.close();
    }
  });
});
