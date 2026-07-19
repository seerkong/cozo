const { expect, test, describe } = require('bun:test');
const { createTestDb } = require('./helpers');

describe('attribute inheritance', () => {
  test('getAttributeDefinitions includes inherited attrs from parent', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Resource', 'Resource');
      await om.defineAttribute(db, 'Resource', 'name', 'String', true);
      await om.defineAttribute(db, 'Resource', 'location', 'String', false);
      await om.defineType(db, 'ExecutableResource', 'Executable Resource', { parentType: 'Resource' });

      const defs = await om.getAttributeDefinitions(db, 'ExecutableResource');
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
      await om.defineType(db, 'Resource', 'Resource');
      await om.defineAttribute(db, 'Resource', 'name', 'String', true);
      await om.defineAttribute(db, 'Resource', 'location', 'String', false);
      await om.defineType(db, 'ExecutableResource', 'Executable Resource', { parentType: 'Resource' });

      // Tighten location from optional to required -- should succeed
      await om.defineAttribute(db, 'ExecutableResource', 'location', 'String', true);

      const defs = await om.getAttributeDefinitions(db, 'ExecutableResource');
      expect(defs.get('location').required).toBe(true);
    } finally {
      db.close();
    }
  });

  test('child cannot loosen inherited required true to false', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Resource', 'Resource');
      await om.defineAttribute(db, 'Resource', 'name', 'String', true);
      await om.defineType(db, 'ExecutableResource', 'Executable Resource', { parentType: 'Resource' });

      await expect(
        om.defineAttribute(db, 'ExecutableResource', 'name', 'String', false)
      ).rejects.toThrow(/Cannot loosen required/);
    } finally {
      db.close();
    }
  });

  test('child cannot change inherited value_type', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Resource', 'Resource');
      await om.defineAttribute(db, 'Resource', 'name', 'String', true);
      await om.defineType(db, 'ExecutableResource', 'Executable Resource', { parentType: 'Resource' });

      await expect(
        om.defineAttribute(db, 'ExecutableResource', 'name', 'Number', true)
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

      // Resource (ancestor) defines created_by as required
      await om.defineType(db, 'Resource', 'Resource');
      await om.defineAttribute(db, 'Resource', 'created_by', 'String', true);

      // ExecutableResource has mixin Auditable + parent Resource
      await om.defineType(db, 'ExecutableResource', 'Executable Resource', {
        parentType: 'Resource',
        mixins: ['Auditable'],
      });

      // Effective created_by should be required (ancestor wins over mixin)
      const defs = await om.getAttributeDefinitions(db, 'ExecutableResource');
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

      await om.defineType(db, 'Resource', 'Resource');
      await om.defineAttribute(db, 'Resource', 'created_by', 'String', true);

      await om.defineType(db, 'ExecutableResource', 'Executable Resource', {
        parentType: 'Resource',
        mixins: ['Auditable'],
      });

      await expect(
        om.defineAttribute(db, 'ExecutableResource', 'created_by', 'String', false)
      ).rejects.toThrow(/Cannot loosen required constraint/);
    } finally {
      db.close();
    }
  });
});
