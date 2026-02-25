const { expect, test, describe } = require('bun:test');

const { CozoDb } = require('../index');
const om = require('../cozo-om');

describe('temporal: initSchema migration for om_property/om_edge', () => {
  test('migrates old om_property and om_edge schemas and preserves data', async () => {
    const db = new CozoDb('mem', '', {});
    try {
      // Simulate an older database with pre-temporal schema.
      await db.run(':create om_property {entity_id, attr_name => value}', {});
      await db.run(':create om_edge {from_id, rel_name, to_id => props}', {});

      await db.run(
        '?[entity_id, attr_name, value] <- [["e:1", "name", "Alice"]] :put om_property {entity_id, attr_name => value}',
        {}
      );
      await db.run(
        '?[from_id, rel_name, to_id, props] <- [["e:1", "knows", "e:2", {"since": 2020}]] :put om_edge {from_id, rel_name, to_id => props}',
        {}
      );

      // Upgrade using the new initSchema.
      await om.initSchema(db);

      // Verify columns include bi-temporal fields.
      const pCols = await db.run('::columns om_property', {});
      const pNames = new Set(pCols.rows.map((r) => r[0]));
      expect(pNames.has('valid_time')).toBe(true);
      expect(pNames.has('tx_time')).toBe(true);

      const eCols = await db.run('::columns om_edge', {});
      const eNames = new Set(eCols.rows.map((r) => r[0]));
      expect(eNames.has('valid_time')).toBe(true);
      expect(eNames.has('tx_time')).toBe(true);

      // Data should be visible at NOW.
      const propNow = await db.run(
        '?[value] := *om_property{ entity_id: $id, attr_name: $attr, value @ "NOW" }',
        { id: 'e:1', attr: 'name' }
      );
      expect(propNow.rows).toEqual([['Alice']]);

      const edgeNow = await db.run(
        '?[props] := *om_edge{ from_id: $from, rel_name: $rel, to_id: $to, props @ "NOW" }',
        { from: 'e:1', rel: 'knows', to: 'e:2' }
      );
      expect(edgeNow.rows).toEqual([[{ since: 2020 }]]);
    } finally {
      db.close();
    }
  });

  test('initSchema is idempotent on an already-upgraded database', async () => {
    const db = new CozoDb('mem', '', {});
    try {
      await om.initSchema(db);
      await expect(om.initSchema(db)).resolves.toBeUndefined();
    } finally {
      db.close();
    }
  });
});
