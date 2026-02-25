const { expect, test, describe } = require('bun:test');

const { createTestDb } = require('./helpers');

describe('OM validation helper APIs', () => {
  test('validatePropertyType preflights value types without writing', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'ValidationPerson', 'Validation person');
      await om.defineAttribute(db, 'ValidationPerson', 'name', 'String', true);
      await om.createEntity(db, 'vp:1', 'ValidationPerson', 'Validation Person');

      await expect(om.validatePropertyType(db, 'vp:1', 'name', 123)).rejects.toThrow(/expects String/i);
      expect(await om.getProperty(db, 'vp:1', 'name')).toBeUndefined();
    } finally {
      db.close();
    }
  });

  test('validateRelation preflights endpoint compatibility without writing', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'ValidationPerson', 'Validation person');
      await om.defineType(db, 'ValidationDepartment', 'Validation department');
      await om.defineRelation(db, 'validation_member_of', 'ValidationPerson', 'ValidationDepartment', true);
      await om.createEntity(db, 'vp:2', 'ValidationPerson', 'Validation Person');
      await om.createEntity(db, 'vd:1', 'ValidationDepartment', 'Validation Department');

      await expect(
        om.validateRelation(db, 'vd:1', 'validation_member_of', 'vp:2')
      ).rejects.toThrow(/expects/i);
      expect((await om.getNeighbors(db, 'vd:1', 'validation_member_of', 'outgoing')).outgoing.length).toBe(0);
    } finally {
      db.close();
    }
  });

  test('validateRequiredProperties returns missing attrs and finalizeEntity enforces completeness', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'FinalizedPerson', 'Finalized person');
      await om.defineAttribute(db, 'FinalizedPerson', 'name', 'String', true);
      await om.createEntity(db, 'fp:1', 'FinalizedPerson', 'Finalized Person');

      expect(await om.validateRequiredProperties(db, 'fp:1')).toEqual(['name']);
      await expect(om.finalizeEntity(db, 'fp:1')).rejects.toThrow(/Missing required property/i);

      await om.setProperty(db, 'fp:1', 'name', 'Final');
      expect(await om.validateRequiredProperties(db, 'fp:1')).toEqual([]);
      await om.finalizeEntity(db, 'fp:1');
    } finally {
      db.close();
    }
  });
});
