const { describe, test, expect } = require('bun:test');

const dsl = require('../cozo-dsl');
const { CozoDb } = require('../index');
const om = require('../cozo-om');

async function assertStoredRelationExists(db, relName, bindings) {
  const built = dsl.query()
    .select(['x'])
    .fromStored(relName, bindings)
    .atom('x = 1')
    .limit(1)
    .build();
  await expect(db.run(built.script, built.params)).resolves.toBeTruthy();
}

async function readSchemaStateDefaultRow(db) {
  // Allow either snake_case or camelCase field for the version column.
  const attempts = [
    { versionField: 'current_version', columnIndex: 0 },
    { versionField: 'currentVersion', columnIndex: 0 },
  ];

  let lastErr = null;
  for (const a of attempts) {
    const built = dsl.query()
      .select([a.versionField])
      .fromStored('om_schema_state', {
        id: dsl.param('id', 'default'),
        [a.versionField]: dsl.var(a.versionField),
      })
      .limit(1)
      .build();
    try {
      const result = await db.run(built.script, built.params);
      const rows = result && Array.isArray(result.rows) ? result.rows : [];
      return rows;
    } catch (e) {
      lastErr = e;
      const code = e && typeof e === 'object' ? e.code : '';
      // If the relation doesn't exist, do not fall back; that's the real failure.
      if (code === 'query::relation_not_found' || code === 'eval::stored_relation_not_found') {
        throw e;
      }
      // Otherwise, try the alternate field name.
    }
  }
  throw lastErr || new Error('Failed to read om_schema_state default row');
}

describe('P1/WAVE-P1-01: schema versioning metadata', () => {
  test('initSchema creates stored relations for schema versioning + alias metadata', async () => {
    const db = new CozoDb('mem', '', {});
    try {
      await om.initSchema(db);

      // Schema versioning metadata relations.
      await assertStoredRelationExists(db, 'om_schema_state', { id: dsl.var('id') });
      await assertStoredRelationExists(db, 'om_schema_version', { version: dsl.var('version') });
      await assertStoredRelationExists(db, 'om_schema_migration', { migration_id: dsl.var('migration_id') });
      await assertStoredRelationExists(db, 'om_schema_snapshot', { version: dsl.var('version') });

      // Alias mapping relations.
      await assertStoredRelationExists(db, 'om_alias_type', { alias: dsl.var('alias') });
      await assertStoredRelationExists(db, 'om_alias_rel', { alias: dsl.var('alias') });
      await assertStoredRelationExists(db, 'om_alias_attr', {
        type_name: dsl.var('type_name'),
        alias_attr: dsl.var('alias_attr'),
      });
    } finally {
      db.close();
    }
  });

  test("initSchema seeds schema state id='default' with current_version=1", async () => {
    const db = new CozoDb('mem', '', {});
    try {
      await om.initSchema(db);

      const rows = await readSchemaStateDefaultRow(db);
      expect(Array.isArray(rows)).toBe(true);
      expect(rows.length).toBe(1);

      const version = rows[0][0];
      expect(version).toBe(1);
    } finally {
      db.close();
    }
  });

  test('schema versioning APIs exist and return version 1', async () => {
    const db = new CozoDb('mem', '', {});
    try {
      await om.initSchema(db);

      expect(typeof om.getSchemaState).toBe('function');
      expect(typeof om.listSchemaVersions).toBe('function');

      const state = await om.getSchemaState(db);
      expect(state && typeof state).toBe('object');
      expect(state.currentVersion).toBe(1);
      expect(state.checksum === '' || state.checksum === null).toBe(true);

      const versions = await om.listSchemaVersions(db);
      expect(Array.isArray(versions)).toBe(true);

      const hasV1 = versions.some((v) => {
        if (v === 1 || v === '1') return true;
        if (!v || typeof v !== 'object') return false;
        return v.version === 1 || v.version === '1';
      });
      expect(hasV1).toBe(true);
    } finally {
      db.close();
    }
  });
});
