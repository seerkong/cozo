const { expect, test, describe } = require('bun:test');

const { createTestDb } = require('./helpers');
const dsl = require('../cozo-dsl');

describe('temporal: Validity attribute type', () => {
  test('defineAttribute supports value_type Validity', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Employee', 'Employee');
      await om.defineAttribute(db, 'Employee', 'valid_from', 'Validity', true);

      const q = dsl
        .query()
        .select(['value_type'])
        .fromStored('om_attr_def', {
          type_name: dsl.param('type_name', 'Employee'),
          attr_name: dsl.param('attr_name', 'valid_from'),
          value_type: dsl.var('value_type'),
          required: dsl.var('_r'),
        })
        .limit(1)
        .build();

      const result = await db.run(q.script, q.params);
      expect(result.rows.length).toBe(1);
      expect(result.rows[0][0]).toBe('Validity');
    } finally {
      db.close();
    }
  });

  test('setProperty stores Validity-typed value as Validity', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Employee', 'Employee');
      await om.defineAttribute(db, 'Employee', 'valid_from', 'Validity', false);
      await om.createEntity(db, 'emp:1', 'Employee', 'Alice');

      const iso = '2026-01-01T00:00:00Z';
      await om.setProperty(db, 'emp:1', 'valid_from', iso);

      const expectedMicros = Date.parse(iso) * 1000;
      const res = await db.run(
        `
?[ts] :=
  *om_property{ entity_id: $entity_id, attr_name: $attr_name, value: v },
  ts = to_int(v)
        `.trim(),
        { entity_id: 'emp:1', attr_name: 'valid_from' }
      );

      expect(res.rows).toEqual([[expectedMicros]]);
    } finally {
      db.close();
    }
  });
});
