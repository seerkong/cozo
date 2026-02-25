const { test, expect } = require('bun:test');
const { CozoDb, om } = require('cozo-lib-bun');

const demo = require('./approval-flow');
const { parseSheetsIntoBatch } = require('../helpers');

const TABLE_NAMES = {
  types: '类型定义',
  attributes: '属性定义',
  relations: '关系定义',
};

function tableMapFromTables(tables) {
  const map = Object.create(null);
  for (const t of Array.isArray(tables) ? tables : []) {
    if (!t || typeof t !== 'object') continue;
    map[t.name] = t;
  }
  return map;
}

function ensureTableShape(table) {
  return table && typeof table === 'object'
    ? { columns: Array.isArray(table.columns) ? table.columns : [], rows: Array.isArray(table.rows) ? table.rows : [] }
    : { columns: [], rows: [] };
}

function buildAttrSchema(tableMap) {
  const attrTable = ensureTableShape(tableMap[TABLE_NAMES.attributes]);
  const schema = new Map();
  for (const row of attrTable.rows) {
    const key = String(row?.attrName || '').trim();
    if (!key) continue;
    schema.set(key, { valueType: String(row?.valueType || '').trim() });
  }
  return schema;
}

async function defineOntologyFromTables(db, tableMap) {
  await om.initSchema(db);

  const typeTable = ensureTableShape(tableMap[TABLE_NAMES.types]);
  for (const row of typeTable.rows) {
    const typeName = String(row?.typeName || '').trim();
    if (!typeName) continue;
    const desc = String(row?.description || '');

    const parentTypeRaw = row?.parent_type;
    const parentType = parentTypeRaw == null || String(parentTypeRaw).trim() === '' ? null : String(parentTypeRaw);

    const mixinsRaw = String(row?.mixins || '').trim();
    const mixins = mixinsRaw ? mixinsRaw.split(',').map((s) => s.trim()).filter(Boolean) : [];

    // Types may reference mixins; ensure they exist.
    for (const m of mixins) {
      await om.defineMixin(db, m, '');
    }

    const opts = {};
    if (parentType) opts.parentType = parentType;
    if (mixins.length) opts.mixins = mixins;

    await om.defineType(db, typeName, desc, opts);
  }

  const attrTable = ensureTableShape(tableMap[TABLE_NAMES.attributes]);
  for (const row of attrTable.rows) {
    const typeName = String(row?.typeName || '').trim();
    const attrName = String(row?.attrName || '').trim();
    const valueType = String(row?.valueType || '').trim();
    if (!typeName || !attrName || !valueType) continue;
    await om.defineAttribute(db, typeName, attrName, valueType, !!row?.required, String(row?.description || ''));
  }

  const relTable = ensureTableShape(tableMap[TABLE_NAMES.relations]);
  for (const row of relTable.rows) {
    const relName = String(row?.relName || '').trim();
    const fromType = String(row?.fromType || '').trim();
    const toType = String(row?.toType || '').trim();
    if (!relName || !fromType || !toType) continue;
    await om.defineRelation(db, relName, fromType, toType, !!row?.directed, String(row?.description || ''));
  }
}

test('approval-flow: timeline query uses om temporal API', async () => {
  const db = new CozoDb();
  try {
    if (om && typeof om.clearRegistry === 'function') {
      om.clearRegistry();
    }

    const tableMap = tableMapFromTables(demo.defaultTables);
    await defineOntologyFromTables(db, tableMap);

    const attrSchema = buildAttrSchema(tableMap);
    const batch = parseSheetsIntoBatch(demo.defaultSheets, attrSchema);
    await om.ingestBatch(db, batch);

    await demo.registerBehaviors(db);

    const q = demo.queries.find((x) => x.queryId === 'timelineAfterApprove');
    expect(q).toBeTruthy();

    const result = await q.run(db);
    expect(result.view).toBe('table');
    expect(result.data).toBeTruthy();
    expect(result.data.columns).toEqual([
      'kind',
      'effective_at',
      'ts_us',
      'is_assert',
      'ts_iso',
      'status',
      'decided_by',
      'note',
    ]);

    const rows = Array.isArray(result.data.rows) ? result.data.rows : [];
    const timeline = rows.filter((r) => r && typeof r.kind === 'string' && r.kind === 'timeline');
    const asOf = rows.filter((r) => r && typeof r.kind === 'string' && r.kind.startsWith('as_of:'));

    expect(timeline.length).toBeGreaterThanOrEqual(2);
    expect(asOf.length).toBe(2);

    const asOfStatuses = new Set(asOf.map((r) => String(r.status)));
    expect(asOfStatuses.has('submitted')).toBe(true);
    expect(asOfStatuses.has('approved')).toBe(true);
  } finally {
    try {
      db.close();
    } catch (_) {
      // ignore
    }
  }
});
