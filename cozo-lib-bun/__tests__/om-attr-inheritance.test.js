const { expect, test, describe } = require('bun:test');
const { createTestDb } = require('./helpers');

describe('attribute inheritance', () => {
  test('getAttributeDefinitions includes inherited attrs from parent', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Asset', 'Asset');
      await om.defineAttribute(db, 'Asset', 'name', 'String', true);
      await om.defineAttribute(db, 'Asset', 'location', 'String', false);
      await om.defineType(db, 'ITAsset', 'IT Asset', { parentType: 'Asset' });

      const defs = await om.getAttributeDefinitions(db, 'ITAsset');
      expect(defs.has('name')).toBe(true);
      expect(defs.get('name').valueType).toBe('String');
      expect(defs.get('name').required).toBe(true);
      expect(defs.has('location')).toBe(true);
      expect(defs.get('location').valueType).toBe('String');
      expect(defs.get('location').required).toBe(false);
    } finally {
      db.close();
    }
  });

  test('child can tighten optional attr to required', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Asset', 'Asset');
      await om.defineAttribute(db, 'Asset', 'name', 'String', true);
      await om.defineAttribute(db, 'Asset', 'location', 'String', false);
      await om.defineType(db, 'ITAsset', 'IT Asset', { parentType: 'Asset' });

      // Tighten location from optional to required -- should succeed
      await om.defineAttribute(db, 'ITAsset', 'location', 'String', true);

      const defs = await om.getAttributeDefinitions(db, 'ITAsset');
      expect(defs.get('location').required).toBe(true);
    } finally {
      db.close();
    }
  });

  test('child cannot loosen inherited required true to false', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Asset', 'Asset');
      await om.defineAttribute(db, 'Asset', 'name', 'String', true);
      await om.defineType(db, 'ITAsset', 'IT Asset', { parentType: 'Asset' });

      await expect(
        om.defineAttribute(db, 'ITAsset', 'name', 'String', false)
      ).rejects.toThrow(/Cannot loosen required/);
    } finally {
      db.close();
    }
  });

  test('child cannot change inherited value_type', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Asset', 'Asset');
      await om.defineAttribute(db, 'Asset', 'name', 'String', true);
      await om.defineType(db, 'ITAsset', 'IT Asset', { parentType: 'Asset' });

      await expect(
        om.defineAttribute(db, 'ITAsset', 'name', 'Number', true)
      ).rejects.toThrow(/Cannot change value_type/);
    } finally {
      db.close();
    }
  });

  test('mixin precedence: ancestor overrides mixin for same attr', async () => {
    const { db, om } = await createTestDb();
    try {
      // Mixin defines created_by as optional
      await om.defineMixin(db, 'Auditable', 'Auditable mixin');
      await om.defineAttribute(db, 'Auditable', 'created_by', 'String', false);

      // Asset (ancestor) defines created_by as required
      await om.defineType(db, 'Asset', 'Asset');
      await om.defineAttribute(db, 'Asset', 'created_by', 'String', true);

      // ITAsset has mixin Auditable + parent Asset
      await om.defineType(db, 'ITAsset', 'IT Asset', {
        parentType: 'Asset',
        mixins: ['Auditable'],
      });

      // Effective created_by should be required (ancestor wins over mixin)
      const defs = await om.getAttributeDefinitions(db, 'ITAsset');
      expect(defs.get('created_by').required).toBe(true);
    } finally {
      db.close();
    }
  });

  test('child cannot loosen inherited required=true even when mixin defines it as optional', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineMixin(db, 'Auditable', 'Auditable mixin');
      await om.defineAttribute(db, 'Auditable', 'created_by', 'String', false);

      await om.defineType(db, 'Asset', 'Asset');
      await om.defineAttribute(db, 'Asset', 'created_by', 'String', true);

      await om.defineType(db, 'ITAsset', 'IT Asset', {
        parentType: 'Asset',
        mixins: ['Auditable'],
      });

      await expect(
        om.defineAttribute(db, 'ITAsset', 'created_by', 'String', false)
      ).rejects.toThrow(/Cannot loosen required constraint/);
    } finally {
      db.close();
    }
  });
});
