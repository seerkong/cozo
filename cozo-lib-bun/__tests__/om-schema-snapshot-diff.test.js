const { describe, test, expect } = require('bun:test');

const dsl = require('../cozo-dsl');
const { createTestDb } = require('./helpers');

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

function isNonEmptyValue(v) {
  if (typeof v === 'string') return v.trim().length > 0;
  if (v && typeof v === 'object') return true;
  return v != null;
}

function tryParseJson(value) {
  if (value == null) return value;
  if (typeof value === 'object') return value;
  if (typeof value !== 'string') return value;
  const s = value.trim();
  if (!s) return value;
  try {
    return JSON.parse(s);
  } catch {
    return value;
  }
}

function getSection(obj, key) {
  if (!obj || typeof obj !== 'object') return undefined;
  if (Object.prototype.hasOwnProperty.call(obj, key)) return obj[key];
  if (obj.schema && typeof obj.schema === 'object' && Object.prototype.hasOwnProperty.call(obj.schema, key)) {
    return obj.schema[key];
  }
  return undefined;
}

function assertSnapshotHasRequiredSections(snapshotPayload) {
  const parsed = tryParseJson(snapshotPayload);

  if (parsed && typeof parsed === 'object') {
    expect(getSection(parsed, 'types')).toBeDefined();
    expect(getSection(parsed, 'attrs')).toBeDefined();
    expect(getSection(parsed, 'rels')).toBeDefined();
    expect(getSection(parsed, 'hierarchy')).toBeDefined();
    expect(getSection(parsed, 'alias')).toBeDefined();

    const permSection = getSection(parsed, 'perm') ?? getSection(parsed, 'permission');
    expect(permSection).toBeDefined();
    return;
  }

  // Fallback: tolerate non-JSON string payloads by key presence.
  expect(typeof parsed).toBe('string');
  const s = String(parsed);
  expect(s).toMatch(/types/i);
  expect(s).toMatch(/attrs/i);
  expect(s).toMatch(/rels/i);
  expect(s).toMatch(/hierarchy/i);
  expect(s).toMatch(/alias/i);
  expect(s).toMatch(/perm|permission/i);
}

async function applyV1ToV2Migration(db, om) {
  const spec = {
    migrationId: 'p2-snap-v2',
    migration_id: 'p2-snap-v2',
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
}

describe('P2/WAVE-P2-02: schema snapshot + diff', () => {
  test('after v1->v2 migration, snapshot is stored and readable', async () => {
    const { db, om } = await createTestDb();
    try {
      expect(typeof om.applySchemaMigration).toBe('function');
      expect(typeof om.readSchemaSnapshot).toBe('function');

      await applyV1ToV2Migration(db, om);

      // Validate snapshot exists via stored relation.
      const row = await readSnapshotRow(db, 2);
      expect(row).toBeTruthy();
      expect(isNonEmptyValue(row.snapshotJson)).toBe(true);

      // Validate snapshot is readable via API.
      const snapshotPayload = await om.readSchemaSnapshot(db, 2);
      expect(isNonEmptyValue(snapshotPayload)).toBe(true);
      assertSnapshotHasRequiredSections(snapshotPayload);
    } finally {
      db.close();
    }
  });

  test('diffSchemaVersions(db, 1, 2) returns non-empty structured diff', async () => {
    const { db, om } = await createTestDb();
    try {
      expect(typeof om.applySchemaMigration).toBe('function');
      expect(typeof om.diffSchemaVersions).toBe('function');

      await applyV1ToV2Migration(db, om);

      const diff = await om.diffSchemaVersions(db, 1, 2);
      expect(diff && typeof diff).toBe('object');
      expect(Object.keys(diff).length).toBeGreaterThan(0);

      // Prefer a precise check if possible, otherwise accept any non-empty diff.
      const text = JSON.stringify(diff);
      const mentionsNewType = /Employee/.test(text);
      const mentionsNewAttr = /department|org_unit/.test(text);
      const hasAnyAdded = (() => {
        const added = diff && typeof diff === 'object' ? diff.added : null;
        if (!added) return false;
        if (Array.isArray(added)) return added.length > 0;
        if (typeof added === 'object') return Object.keys(added).length > 0;
        return false;
      })();

      expect(mentionsNewType || mentionsNewAttr || hasAnyAdded).toBe(true);
    } finally {
      db.close();
    }
  });
});
