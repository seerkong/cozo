const { describe, test, expect } = require('bun:test');

const dsl = require('../cozo-dsl');
const { createTestDb } = require('./helpers');

async function readMigrationRow(db, migrationId) {
  const built = dsl
    .query()
    .select(['from_version', 'to_version', 'status', 'applied_at', 'error', 'summary_json'])
    .fromStored('om_schema_migration', {
      migration_id: dsl.param('migration_id', migrationId),
      from_version: dsl.var('from_version'),
      to_version: dsl.var('to_version'),
      applied_at: dsl.var('applied_at'),
      applied_by: dsl.var('_applied_by'),
      status: dsl.var('status'),
      error: dsl.var('error'),
      summary_json: dsl.var('summary_json'),
    })
    .limit(1)
    .build();

  const result = await db.run(built.script, built.params);
  const rows = result && Array.isArray(result.rows) ? result.rows : [];
  if (!rows.length) return null;
  const [fromVersion, toVersion, status, appliedAt, error, summaryJson] = rows[0];
  return { fromVersion, toVersion, status, appliedAt, error, summaryJson };
}

async function readSnapshotRow(db, version) {
  const built = dsl
    .query()
    .select(['snapshot_json'])
    .fromStored('om_schema_snapshot', {
      version: dsl.param('version', version),
      snapshot_json: dsl.var('snapshot_json'),
    })
    .limit(1)
    .build();

  const result = await db.run(built.script, built.params);
  const rows = result && Array.isArray(result.rows) ? result.rows : [];
  if (!rows.length) return null;
  return { snapshotJson: rows[0][0] };
}

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

function isNonEmptySnapshot(value) {
  if (typeof value === 'string') return value.trim().length > 0;
  if (value && typeof value === 'object') return true;
  return value != null;
}

describe('P2/WAVE-P2-01 (T2.1.1): applySchemaMigration', () => {
  test('om.applySchemaMigration(db, spec) exists', async () => {
    const { db, om } = await createTestDb();
    try {
      expect(typeof om.applySchemaMigration).toBe('function');
    } finally {
      db.close();
    }
  });

  test('applies v1->v2 migration, writes migration log + snapshot + alias mapping', async () => {
    const { db, om } = await createTestDb();
    try {
      if (typeof om.applySchemaMigration !== 'function') {
        throw new Error('applySchemaMigration is not implemented');
      }

      const state0 = await om.getSchemaState(db);
      expect(state0.currentVersion).toBe(1);

      const spec = {
        migrationId: 'm1',
        migration_id: 'm1',
        fromVersion: 1,
        from_version: 1,
        toVersion: 2,
        to_version: 2,
        label: 'v2: add Employee + rename org_unit -> department',
        strict: true,
        steps: [
          {
            kind: 'addType',
            type: 'addType',
            typeName: 'Employee',
            type_name: 'Employee',
            description: 'Employee',
          },
          {
            kind: 'addAttribute',
            type: 'addAttribute',
            typeName: 'Employee',
            type_name: 'Employee',
            attrName: 'org_unit',
            attr_name: 'org_unit',
            valueType: 'String',
            value_type: 'String',
            required: false,
          },
          {
            kind: 'renameAttribute',
            type: 'renameAttribute',
            typeName: 'Employee',
            type_name: 'Employee',
            fromAttr: 'org_unit',
            from_attr: 'org_unit',
            toAttr: 'department',
            to_attr: 'department',
          },
        ],
      };

      await om.applySchemaMigration(db, spec);

      const state1 = await om.getSchemaState(db);
      expect(state1.currentVersion).toBe(2);

      const versions = await om.listSchemaVersions(db);
      const hasV2 = Array.isArray(versions)
        ? versions.some((v) => {
            if (v === 2 || v === '2') return true;
            if (!v || typeof v !== 'object') return false;
            return v.version === 2 || v.version === '2';
          })
        : false;
      expect(hasV2).toBe(true);

      const mig = await readMigrationRow(db, 'm1');
      expect(mig).toBeTruthy();
      expect(mig.fromVersion).toBe(1);
      expect(mig.toVersion).toBe(2);
      expect(String(mig.status || '')).toMatch(/applied|ok|success|done/i);
      expect(String(mig.appliedAt || '')).not.toBe('');

      const snap = await readSnapshotRow(db, 2);
      expect(snap).toBeTruthy();
      expect(isNonEmptySnapshot(snap.snapshotJson)).toBe(true);

      // Alias mapping should preserve old->new for renamed attribute.
      const canonical = await readAliasAttrCanonical(db, 'Employee', 'org_unit');
      expect(canonical).toBe('department');
    } finally {
      db.close();
    }
  });

  test('strict preflight rejects incompatible valueType change', async () => {
    const { db, om } = await createTestDb();
    try {
      if (typeof om.applySchemaMigration !== 'function') {
        throw new Error('applySchemaMigration is not implemented');
      }

      await om.defineType(db, 'Employee', 'Employee');
      await om.defineAttribute(db, 'Employee', 'code', 'String', false);

      await om.createEntity(db, 'emp:vtype-1', 'Employee', 'Alice');
      await om.setProperty(db, 'emp:vtype-1', 'code', 'not-a-number', {
        validTime: '2000-01-01T00:00:00Z',
      });

      const spec = {
        migrationId: 'm2',
        migration_id: 'm2',
        fromVersion: 1,
        from_version: 1,
        toVersion: 2,
        to_version: 2,
        label: 'v2: change Employee.code String -> Number',
        strict: true,
        steps: [
          {
            kind: 'changeAttribute',
            type: 'changeAttribute',
            typeName: 'Employee',
            type_name: 'Employee',
            attrName: 'code',
            attr_name: 'code',
            valueType: 'Number',
            value_type: 'Number',
          },
        ],
      };

      await expect(om.applySchemaMigration(db, spec)).rejects.toThrow(
        /value\s*type|valuetype|type\s*mismatch|mismatch|convert|cannot\s+change/i
      );

      const state = await om.getSchemaState(db);
      expect(state.currentVersion).toBe(1);
    } finally {
      db.close();
    }
  });
});
