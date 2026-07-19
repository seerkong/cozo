const { describe, test, expect } = require('bun:test');

const dsl = require('../cozo-dsl');
const om = require('../cozo-om');
const { createTestDb } = require('./helpers');

async function createOrderWorld() {
  const { db } = await createTestDb();
  await om.defineType(db, 'Order', 'Order');
  await om.defineType(db, 'Shipment', 'Shipment');
  await om.defineRelation(db, 'has_shipment', 'Order', 'Shipment', true);
  await om.defineAttribute(db, 'Order', 'status', 'String', false);
  return db;
}

describe('OM-027: existential rules join schema snapshot/diff/rollback (T4.1)', () => {
  test('snapshot includes existential rule definitions', async () => {
    const db = await createOrderWorld();
    await om.defineExistentialRule(db, 'order_needs_shipment', {
      forEach: { type: 'Order' },
      exists: { rel: 'has_shipment', toType: 'Shipment' },
      message: 'm1',
    });

    await om.writeSchemaSnapshot(db, 1);
    const snapshot = await om.readSchemaSnapshot(db, 1);

    const rows = snapshot.schema.om_existential_rule_def;
    expect(Array.isArray(rows)).toBe(true);
    expect(rows.length).toBe(1);
    expect(rows[0][0]).toBe('order_needs_shipment');
  });

  test('rollback restores the historical rule definition', async () => {
    const db = await createOrderWorld();
    await om.defineExistentialRule(db, 'r1', {
      forEach: { type: 'Order' },
      exists: { rel: 'has_shipment', toType: 'Shipment' },
      message: 'v1 message',
    });

    await om.applySchemaMigration(db, {
      migrationId: 'mig-ex-' + Math.random().toString(36).slice(2, 8),
      fromVersion: 1,
      toVersion: 2,
      steps: [{ kind: 'addType', typeName: 'Invoice', description: 'Invoice' }],
    });
    await om.defineExistentialRule(db, 'r1', {
      forEach: { type: 'Order', where: [{ attr: 'status', op: '=', value: 'confirmed' }] },
      exists: { rel: 'has_shipment', toType: 'Shipment' },
      message: 'v2 message',
    });
    await om.writeSchemaSnapshot(db, 2);

    await om.rollbackSchema(db, 1);

    const rules = await om.listExistentialRules(db);
    expect(rules.length).toBe(1);
    expect(rules[0].message).toBe('v1 message');
    expect(rules[0].spec.forEach.where).toBeUndefined();
  });

  test('diffSchemaVersions reports rule changes', async () => {
    const db = await createOrderWorld();
    await om.defineExistentialRule(db, 'r1', {
      forEach: { type: 'Order' },
      exists: { rel: 'has_shipment', toType: 'Shipment' },
      message: 'old',
    });

    await om.applySchemaMigration(db, {
      migrationId: 'mig-ex-' + Math.random().toString(36).slice(2, 8),
      fromVersion: 1,
      toVersion: 2,
      steps: [{ kind: 'addType', typeName: 'Invoice', description: 'Invoice' }],
    });
    await om.defineExistentialRule(db, 'r1', {
      forEach: { type: 'Order' },
      exists: { rel: 'has_shipment', toType: 'Shipment' },
      message: 'new',
    });
    await om.defineExistentialRule(db, 'r2', {
      forEach: { type: 'Order' },
      exists: { rel: 'has_shipment', toType: 'Shipment' },
    });
    await om.writeSchemaSnapshot(db, 2);

    const diff = await om.diffSchemaVersions(db, 1, 2);
    const ruleDiff = diff.schema.om_existential_rule_def;
    expect(ruleDiff.added.map((r) => r.rule_name)).toEqual(['r2']);
    expect(ruleDiff.updated.length).toBe(1);
    expect(ruleDiff.updated[0].key.rule_name).toBe('r1');
  });

  test('rollback tolerates legacy snapshots without a rules section', async () => {
    const db = await createOrderWorld();
    await om.defineExistentialRule(db, 'r1', {
      forEach: { type: 'Order' },
      exists: { rel: 'has_shipment', toType: 'Shipment' },
    });

    // Simulate a legacy snapshot written before existential rules existed.
    const snapshot = await om.readSchemaSnapshot(db, 1).catch(() => null);
    const legacy = {
      version: 1,
      createdAt: new Date().toISOString(),
      schema: {
        om_type: (snapshot && snapshot.schema.om_type) || [],
        om_mixin: [],
        om_type_mixin: [],
        om_attr_def: (snapshot && snapshot.schema.om_attr_def) || [],
        om_rel_def: (snapshot && snapshot.schema.om_rel_def) || [],
        om_alias_type: [],
        om_alias_rel: [],
        om_alias_attr: [],
      },
    };
    const built = dsl.query()
      .input({
        version: dsl.param('version', 1),
        snapshot_json: dsl.param('snapshot_json', JSON.stringify(legacy)),
      })
      .put('om_schema_snapshot', ['version'], ['snapshot_json'])
      .build();
    await db.run(built.script, built.params);

    // Move to v2 first so the rollback actually runs.
    await om.applySchemaMigration(db, {
      migrationId: 'mig-ex-' + Math.random().toString(36).slice(2, 8),
      fromVersion: 1,
      toVersion: 2,
      steps: [{ kind: 'addType', typeName: 'Invoice', description: 'Invoice' }],
    });

    const result = await om.rollbackSchema(db, 1, {
      strict: false,
      legacyBehaviorPolicy: 'clear',
    });
    expect(result.ok).toBe(true);
    // Legacy snapshot has no rules section -> restored as empty set; behavior
    // legacy omission is resolved explicitly by D8 policy.
    expect(await om.listExistentialRules(db)).toEqual([]);
  });
});

async function putAliasRel(db, alias, canonical) {
  const built = dsl.query()
    .input({
      alias: dsl.param('alias', alias),
      canonical: dsl.param('canonical', canonical),
    })
    .put('om_alias_rel', ['alias'], ['canonical'])
    .build();
  await db.run(built.script, built.params);
}

describe('OM-027: alias stability and backward compatibility (T4.2)', () => {
  test('rule keeps working after the relation is renamed via alias', async () => {
    const db = await createOrderWorld();
    await om.defineExistentialRule(db, 'order_needs_shipment', {
      forEach: { type: 'Order' },
      exists: { rel: 'has_shipment', toType: 'Shipment' },
    });

    // o_old satisfied with an edge stored under the pre-rename name.
    await om.createEntity(db, 'o_old', 'Order', 'old order');
    await om.createEntity(db, 's_old', 'Shipment', 'old shipment');
    await om.linkEntities(db, 'o_old', 'has_shipment', 's_old');

    // Rename has_shipment -> fulfilled_by: new canonical rel def + alias row.
    await om.defineRelation(db, 'fulfilled_by', 'Order', 'Shipment', true);
    await putAliasRel(db, 'has_shipment', 'fulfilled_by');
    om.invalidateAliasCache(db);

    // o_new satisfied via the new canonical name; o_missing violates.
    await om.createEntity(db, 'o_new', 'Order', 'new order');
    await om.createEntity(db, 's_new', 'Shipment', 'new shipment');
    await om.linkEntities(db, 'o_new', 'fulfilled_by', 's_new');
    await om.createEntity(db, 'o_missing', 'Order', 'missing');

    const violations = await om.checkExistentialRules(db);
    expect(violations.map((v) => v.entityId)).toEqual(['o_missing']);
  });

  test('no rules defined: check and apply are no-ops', async () => {
    const db = await createOrderWorld();
    await om.createEntity(db, 'o1', 'Order', 'order 1');

    expect(await om.checkExistentialRules(db)).toEqual([]);
    const result = await om.applyExistentialRules(db);
    expect(result.created).toEqual([]);
    expect(result.reachedFixpoint).toBe(true);
  });
});
