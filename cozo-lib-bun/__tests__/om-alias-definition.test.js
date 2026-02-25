const { describe, test, expect } = require('bun:test');

const { createTestDb } = require('./helpers');

describe('OM governance API parity: public alias definitions', () => {
  test('defineTypeAlias invalidates a previously cached unresolved name and canonicalizes entity writes', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Employee', 'Employee');

      expect(await om.resolveType(db, 'Staff')).toBe('Staff');
      await om.defineTypeAlias(db, 'Staff', 'Employee');

      expect(await om.resolveType(db, 'Staff')).toBe('Employee');
      await om.createEntity(db, 'employee:1', 'Staff', 'Ada');
      expect(await om.getEntityType(db, 'employee:1')).toBe('Employee');
    } finally {
      db.close();
    }
  });

  test('defineRelationAlias and defineAttributeAlias author aliases used by object writes', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Employee', 'Employee');
      await om.defineType(db, 'Department', 'Department');
      await om.defineAttribute(db, 'Employee', 'org_unit', 'String', false);
      await om.defineRelation(db, 'works_in', 'Employee', 'Department', true);

      expect(await om.resolveRel(db, 'member_of')).toBe('member_of');
      expect(await om.resolveAttr(db, 'Employee', 'department')).toBe('department');
      await om.defineRelationAlias(db, 'member_of', 'works_in');
      await om.defineAttributeAlias(db, 'Employee', 'department', 'org_unit');
      await om.createEntity(db, 'employee:2', 'Employee', 'Grace');
      await om.createEntity(db, 'department:1', 'Department', 'Research');

      await om.setProperty(db, 'employee:2', 'department', 'Research');
      await om.linkEntities(db, 'employee:2', 'member_of', 'department:1');

      expect(await om.resolveRel(db, 'member_of')).toBe('works_in');
      expect(await om.resolveAttr(db, 'Employee', 'department')).toBe('org_unit');
      expect(await om.getProperty(db, 'employee:2', 'org_unit')).toBe('Research');
      expect((await om.getNeighbors(db, 'employee:2', 'works_in')).outgoing.map((item) => item.entityId)).toEqual(['department:1']);
    } finally {
      db.close();
    }
  });

  test('public alias definitions upsert and preserve resolution-time cycle errors', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Employee', 'Employee');
      await om.defineType(db, 'Contractor', 'Contractor');

      await om.defineTypeAlias(db, 'Worker', 'Employee');
      await om.defineTypeAlias(db, 'Worker', 'Contractor');
      expect(await om.resolveType(db, 'Worker')).toBe('Contractor');

      await om.defineTypeAlias(db, 'A', 'B');
      await om.defineTypeAlias(db, 'B', 'A');
      await expect(om.resolveType(db, 'A')).rejects.toThrow(/cycle/i);
    } finally {
      db.close();
    }
  });

  test('public alias definitions reject empty names', async () => {
    const { db, om } = await createTestDb();
    try {
      await expect(om.defineTypeAlias(db, '', 'Employee')).rejects.toThrow(/alias/i);
      await expect(om.defineRelationAlias(db, 'member_of', '')).rejects.toThrow(/canonical/i);
      await expect(om.defineAttributeAlias(db, '', 'department', 'org_unit')).rejects.toThrow(/typeName/i);
    } finally {
      db.close();
    }
  });
});
