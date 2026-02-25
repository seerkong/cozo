const { test, expect } = require('bun:test');
const { CozoDb, om } = require('cozo-lib-bun');

const demo = require('./org-timeline');
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

test('org-timeline: snapshot and timeline queries work', async () => {
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

    const snapshot = demo.queries.find((q) => q.queryId === 'snapshot_2024_12');
    expect(snapshot).toBeTruthy();
    const snapResult = await snapshot.run(db);
    expect(snapResult.view).toBe('table');
    expect(snapResult.data.columns).toEqual([
      'as_of',
      'employee_id',
      'employee',
      'department_id',
      'department',
      'team_id',
      'team',
    ]);
    expect(Array.isArray(snapResult.data.rows)).toBe(true);
    expect(snapResult.data.rows.length).toBeGreaterThanOrEqual(3);

    const timeline = demo.queries.find((q) => q.queryId === 'alice_timeline');
    expect(timeline).toBeTruthy();
    const tlResult = await timeline.run(db);
    expect(tlResult.view).toBe('table');
    expect(tlResult.data.columns).toEqual([
      'employee_id',
      'valid_time',
      'event',
      'department_id',
      'department_label',
    ]);
    expect(Array.isArray(tlResult.data.rows)).toBe(true);
    expect(tlResult.data.rows.length).toBeGreaterThanOrEqual(3);
  } finally {
    try {
      db.close();
    } catch (_) {
      // ignore
    }
  }
});
