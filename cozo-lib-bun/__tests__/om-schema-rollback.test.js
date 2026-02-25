const { describe, test, expect } = require('bun:test');

const dsl = require('../cozo-dsl');
const { createTestDb } = require('./helpers');

async function readAliasAttrCanonical(db, typeName, aliasAttr) {
  const built = dsl
    .query()
    .select(['canonical_attr'])
    .fromStored('om_alias_attr', {
      type_name: dsl.param('type_name', typeName),
      alias_attr: dsl.param('alias_attr', aliasAttr),
      canonical_attr: dsl.var('canonical_attr'),
    })
    .limit(1)
    .build();

  const result = await db.run(built.script, built.params);
  const rows = result && Array.isArray(result.rows) ? result.rows : [];
  if (!rows.length) return null;
  return String(rows[0][0] || '').trim();
}

async function listAttrNamesForType(db, typeName) {
  const built = dsl
    .query()
    .select(['attr_name'])
    .fromStored('om_attr_def', {
      type_name: dsl.param('type_name', typeName),
      attr_name: dsl.var('attr_name'),
      value_type: dsl.var('_vt'),
      required: dsl.var('_req'),
    })
    .order('attr_name')
    .build();

  const result = await db.run(built.script, built.params);
  const rows = result && Array.isArray(result.rows) ? result.rows : [];
  return rows.map((r) => String(r[0] || '').trim()).filter(Boolean);
}

describe('P2/WAVE-P2-03: rollbackSchema (strict/force)', () => {
  test('strict rollback blocks when target schema is tighter than current data', async () => {
    const { db, om } = await createTestDb();
    try {
      expect(typeof om.applySchemaMigration).toBe('function');
      expect(typeof om.rollbackSchema).toBe('function');

      // v1: Employee.level is required Number
      await om.defineType(db, 'Employee', 'Employee');
      await om.defineAttribute(db, 'Employee', 'level', 'Number', true);

      // v1 -> v2: loosen Employee.level to optional String
      await om.applySchemaMigration(db, {
        migrationId: 'rb-tighten-1',
        fromVersion: 1,
        toVersion: 2,
        strict: true,
        steps: [
          {
            kind: 'changeAttribute',
            typeName: 'Employee',
            attrName: 'level',
            valueType: 'String',
            required: false,
          },
        ],
      });

      // Data that is valid in v2 but not in v1.
      await om.createEntity(db, 'emp:rb-1', 'Employee', 'Alice');
      await om.setProperty(db, 'emp:rb-1', 'level', 'not-a-number');
      await om.createEntity(db, 'emp:rb-2', 'Employee', 'Bob');
      // No level property (allowed in v2, required in v1)

      await expect(om.rollbackSchema(db, 1, { strict: true })).rejects.toThrow(
        /strict\s+rollback\s+blocked|violates\s+schema|missing\s+required|expects/i
      );

      const state = await om.getSchemaState(db);
      expect(state.currentVersion).toBe(2);
    } finally {
      db.close();
    }
  });

  test('force rollback succeeds and returns diagnostics; schema state updates', async () => {
    const { db, om } = await createTestDb();
    try {
      expect(typeof om.applySchemaMigration).toBe('function');
      expect(typeof om.rollbackSchema).toBe('function');

      // v1: Employee.level is required Number
      await om.defineType(db, 'Employee', 'Employee');
      await om.defineAttribute(db, 'Employee', 'level', 'Number', true);

      // v1 -> v2: loosen Employee.level to optional String
      await om.applySchemaMigration(db, {
        migrationId: 'rb-force-1',
        fromVersion: 1,
        toVersion: 2,
        strict: true,
        steps: [
          {
            kind: 'changeAttribute',
            typeName: 'Employee',
            attrName: 'level',
            valueType: 'String',
            required: false,
          },
        ],
      });

      await om.createEntity(db, 'emp:force-1', 'Employee', 'Alice');
      await om.setProperty(db, 'emp:force-1', 'level', 'not-a-number');

      const res = await om.rollbackSchema(db, 1, { strict: false });
      expect(res && typeof res).toBe('object');
      expect(res.ok).toBe(true);
      expect(res.strict).toBe(false);
      expect(res.targetVersion).toBe(1);
      expect(res.fromVersion).toBe(2);
      expect(Array.isArray(res.diagnostics)).toBe(true);
      expect(res.diagnostics.length).toBeGreaterThan(0);

      const state = await om.getSchemaState(db);
      expect(state.currentVersion).toBe(1);
    } finally {
      db.close();
    }
  });

  test('alias mapping restoration: rollback restores/removes alias mapping after rename migration', async () => {
    const { db, om } = await createTestDb();
    try {
      expect(typeof om.applySchemaMigration).toBe('function');
      expect(typeof om.rollbackSchema).toBe('function');

      // v1: Employee.org_unit exists
      await om.defineType(db, 'Employee', 'Employee');
      await om.defineAttribute(db, 'Employee', 'org_unit', 'String', false);

      // v1 -> v2: rename org_unit -> department (creates alias org_unit -> department)
      await om.applySchemaMigration(db, {
        migrationId: 'rb-alias-1',
        fromVersion: 1,
        toVersion: 2,
        strict: true,
        steps: [
          {
            kind: 'renameAttribute',
            typeName: 'Employee',
            fromAttr: 'org_unit',
            toAttr: 'department',
          },
        ],
      });

      const canonicalV2 = await readAliasAttrCanonical(db, 'Employee', 'org_unit');
      expect(canonicalV2).toBe('department');
      const attrsV2 = await listAttrNamesForType(db, 'Employee');
      expect(attrsV2.includes('department')).toBe(true);
      expect(attrsV2.includes('org_unit')).toBe(false);

      // Roll back to v1: should remove alias and restore original attr def.
      const res = await om.rollbackSchema(db, 1, { strict: true });
      expect(res.ok).toBe(true);
      expect(res.targetVersion).toBe(1);
      expect(res.fromVersion).toBe(2);

      const canonicalV1 = await readAliasAttrCanonical(db, 'Employee', 'org_unit');
      expect(canonicalV1).toBe(null);
      const attrsV1 = await listAttrNamesForType(db, 'Employee');
      expect(attrsV1.includes('org_unit')).toBe(true);
      expect(attrsV1.includes('department')).toBe(false);
    } finally {
      db.close();
    }
  });
});
