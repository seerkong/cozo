const { expect, test, describe } = require('bun:test');

const { CozoDb } = require('../index');
const { createTestDb } = require('./helpers');
const om = require('../cozo-om');

describe('temporal: om_property bi-temporal storage', () => {
  test('initSchema creates bi-temporal om_property with valid_time and tx_time', async () => {
    const db = new CozoDb('mem', '', {});
    try {
      await om.initSchema(db);
      const res = await db.run('::columns om_property', {});
      const byName = new Map(res.rows.map((r) => [r[0], { isKey: r[1], index: r[2], type: r[3] }]));

      expect(byName.get('entity_id')?.isKey).toBe(true);
      expect(byName.get('entity_id')?.type).toBe('String');
      expect(byName.get('attr_name')?.isKey).toBe(true);
      expect(byName.get('attr_name')?.type).toBe('String');

      expect(byName.get('valid_time')?.isKey).toBe(true);
      expect(byName.get('valid_time')?.type).toBe('Validity');

      expect(byName.get('tx_time')?.isKey).toBe(false);
      expect(byName.get('tx_time')?.type).toBe('String');
    } finally {
      db.close();
    }
  });

  test('setProperty writes multiple versions and getProperty reads @ NOW effective value', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Employee', 'Employee');
      await om.defineAttribute(db, 'Employee', 'department', 'String', false);
      await om.createEntity(db, 'emp:1', 'Employee', 'Alice');

      await om.setProperty(db, 'emp:1', 'department', 'Engineering', {
        validTime: '2000-01-01T00:00:00Z',
      });
      await om.setProperty(db, 'emp:1', 'department', 'Product', {
        validTime: '2001-01-01T00:00:00Z',
      });
      await om.setProperty(db, 'emp:1', 'department', 'FutureDept', {
        validTime: '2100-01-01T00:00:00Z',
      });

      const nowVal = await om.getProperty(db, 'emp:1', 'department');
      expect(nowVal).toBe('Product');

      // Raw time-travel query sanity check: @ END should see the latest recorded value.
      const endRes = await db.run(
        '?[value] := *om_property{ entity_id: $id, attr_name: $attr, value @ "END" }',
        { id: 'emp:1', attr: 'department' }
      );
      expect(endRes.rows).toEqual([['FutureDept']]);

      // Ensure history is preserved (3 versions exist)
      const allRes = await db.run(
        '?[valid_time] := *om_property{ entity_id: $id, attr_name: $attr, valid_time }',
        { id: 'emp:1', attr: 'department' }
      );
      expect(allRes.rows.length).toBe(3);
    } finally {
      db.close();
    }
  });

  test('getEntityView returns properties effective @ NOW (future versions are ignored)', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Employee', 'Employee');
      await om.defineAttribute(db, 'Employee', 'department', 'String', false);
      await om.createEntity(db, 'emp:2', 'Employee', 'Bob');

      await om.setProperty(db, 'emp:2', 'department', 'Eng', { validTime: '2000-01-01T00:00:00Z' });
      await om.setProperty(db, 'emp:2', 'department', 'Future', { validTime: '2100-01-01T00:00:00Z' });

      const view = await om.getEntityView(db, 'emp:2');
      expect(view.properties.department).toBe('Eng');
    } finally {
      db.close();
    }
  });
});
