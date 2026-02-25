const { describe, test, expect } = require('bun:test');

const dsl = require('../cozo-dsl');
const { CozoDb } = require('../index');
const om = require('../cozo-om');
const { createTestDb } = require('./helpers');

async function createOrderShipmentDb() {
  const { db } = await createTestDb();
  await om.defineType(db, 'Order', 'Order');
  await om.defineType(db, 'Shipment', 'Shipment');
  await om.defineRelation(db, 'has_shipment', 'Order', 'Shipment', true);
  return db;
}

describe('OM-024: existential rule definition and persistence', () => {
  test('initSchema creates om_existential_rule_def stored relation', async () => {
    const db = new CozoDb('mem', '', {});
    await om.initSchema(db);

    const built = dsl.query()
      .select(['x'])
      .fromStored('om_existential_rule_def', { rule_name: dsl.var('rule_name') })
      .atom('x = 1')
      .limit(1)
      .build();
    await expect(db.run(built.script, built.params)).resolves.toBeTruthy();
  });

  test('initSchema is idempotent and preserves existing rule definitions', async () => {
    const db = await createOrderShipmentDb();
    await om.defineExistentialRule(db, 'order_must_have_shipment', {
      forEach: { type: 'Order' },
      exists: { rel: 'has_shipment', toType: 'Shipment' },
      mode: 'check',
      message: 'confirmed orders need a shipment',
    });

    await om.initSchema(db);

    const rules = await om.listExistentialRules(db);
    expect(rules.length).toBe(1);
    expect(rules[0].ruleName).toBe('order_must_have_shipment');
    expect(rules[0].message).toBe('confirmed orders need a shipment');
  });

  test('defineExistentialRule persists and listExistentialRules returns it', async () => {
    const db = await createOrderShipmentDb();

    await om.defineExistentialRule(db, 'order_must_have_shipment', {
      forEach: { type: 'Order' },
      exists: { rel: 'has_shipment', toType: 'Shipment' },
      mode: 'check',
      message: 'every order must have a shipment',
    });

    const rules = await om.listExistentialRules(db);
    expect(rules.length).toBe(1);
    const rule = rules[0];
    expect(rule.ruleName).toBe('order_must_have_shipment');
    expect(rule.mode).toBe('check');
    expect(rule.enabled).toBe(true);
    expect(rule.message).toBe('every order must have a shipment');
    expect(rule.spec.forEach.type).toBe('Order');
    expect(rule.spec.exists.rel).toBe('has_shipment');
    expect(rule.spec.exists.toType).toBe('Shipment');
    expect(rule.spec.exists.direction).toBe('out');
  });

  test('redefining the same rule name is an upsert', async () => {
    const db = await createOrderShipmentDb();

    await om.defineExistentialRule(db, 'r1', {
      forEach: { type: 'Order' },
      exists: { rel: 'has_shipment', toType: 'Shipment' },
      mode: 'check',
    });
    await om.defineExistentialRule(db, 'r1', {
      forEach: { type: 'Order', where: [{ attr: 'status', op: '=', value: 'confirmed' }] },
      exists: { rel: 'has_shipment', toType: 'Shipment' },
      mode: 'check',
      message: 'updated',
    });

    const rules = await om.listExistentialRules(db);
    expect(rules.length).toBe(1);
    expect(rules[0].message).toBe('updated');
    expect(rules[0].spec.forEach.where).toEqual([{ attr: 'status', op: '=', value: 'confirmed' }]);
  });

  test('listExistentialRules returns [] on a fresh schema', async () => {
    const { db } = await createTestDb();
    const rules = await om.listExistentialRules(db);
    expect(rules).toEqual([]);
  });

  test('enabled flag is persisted and returned', async () => {
    const db = await createOrderShipmentDb();
    await om.defineExistentialRule(db, 'r_disabled', {
      forEach: { type: 'Order' },
      exists: { rel: 'has_shipment', toType: 'Shipment' },
      enabled: false,
    });
    const rules = await om.listExistentialRules(db);
    expect(rules[0].enabled).toBe(false);
  });
});

describe('OM-024: invalid spec rejection (T1.2)', () => {
  test('rejects spec without forEach.type', async () => {
    const db = await createOrderShipmentDb();
    await expect(
      om.defineExistentialRule(db, 'bad', { forEach: {}, exists: { rel: 'has_shipment', toType: 'Shipment' } })
    ).rejects.toThrow(/forEach\.type/);
  });

  test('rejects spec without exists.rel or exists.toType', async () => {
    const db = await createOrderShipmentDb();
    await expect(
      om.defineExistentialRule(db, 'bad', { forEach: { type: 'Order' }, exists: { toType: 'Shipment' } })
    ).rejects.toThrow(/exists\.rel/);
    await expect(
      om.defineExistentialRule(db, 'bad', { forEach: { type: 'Order' }, exists: { rel: 'has_shipment' } })
    ).rejects.toThrow(/exists\.toType/);
  });

  test('rejects unknown forEach.type / exists.toType / exists.rel', async () => {
    const db = await createOrderShipmentDb();
    await expect(
      om.defineExistentialRule(db, 'bad', { forEach: { type: 'Nope' }, exists: { rel: 'has_shipment', toType: 'Shipment' } })
    ).rejects.toThrow(/Unknown type/);
    await expect(
      om.defineExistentialRule(db, 'bad', { forEach: { type: 'Order' }, exists: { rel: 'has_shipment', toType: 'Nope' } })
    ).rejects.toThrow(/Unknown type/);
    await expect(
      om.defineExistentialRule(db, 'bad', { forEach: { type: 'Order' }, exists: { rel: 'nope_rel', toType: 'Shipment' } })
    ).rejects.toThrow(/Unknown relation/);
  });

  test('rejects invalid mode and invalid where op', async () => {
    const db = await createOrderShipmentDb();
    await expect(
      om.defineExistentialRule(db, 'bad', {
        forEach: { type: 'Order' },
        exists: { rel: 'has_shipment', toType: 'Shipment' },
        mode: 'magic',
      })
    ).rejects.toThrow(/mode/);
    await expect(
      om.defineExistentialRule(db, 'bad', {
        forEach: { type: 'Order', where: [{ attr: 'status', op: '~', value: 'x' }] },
        exists: { rel: 'has_shipment', toType: 'Shipment' },
      })
    ).rejects.toThrow(/where/);
  });

  test('rejects where entry without an explicit value (silent dead-rule guard)', async () => {
    const db = await createOrderShipmentDb();
    // JSON persistence drops undefined keys, so an omitted value would be
    // stored as { attr, op } and the rule would silently never match.
    await expect(
      om.defineExistentialRule(db, 'bad', {
        forEach: { type: 'Order', where: [{ attr: 'status', op: '=' }] },
        exists: { rel: 'has_shipment', toType: 'Shipment' },
      })
    ).rejects.toThrow(/require an explicit value/);
    await expect(
      om.defineExistentialRule(db, 'bad', {
        forEach: { type: 'Order', where: [{ attr: 'status', op: '=', value: undefined }] },
        exists: { rel: 'has_shipment', toType: 'Shipment' },
      })
    ).rejects.toThrow(/require an explicit value/);
    expect(await om.listExistentialRules(db)).toEqual([]);

    // null is a representable JSON value and stays accepted.
    await om.defineExistentialRule(db, 'r_null_ok', {
      forEach: { type: 'Order', where: [{ attr: 'status', op: '=', value: null }] },
      exists: { rel: 'has_shipment', toType: 'Shipment' },
    });
    const rules = await om.listExistentialRules(db);
    expect(rules[0].spec.forEach.where).toEqual([{ attr: 'status', op: '=', value: null }]);
  });

  test('rejected define does not persist anything', async () => {
    const db = await createOrderShipmentDb();
    await om
      .defineExistentialRule(db, 'bad', { forEach: { type: 'Nope' }, exists: { rel: 'has_shipment', toType: 'Shipment' } })
      .catch(() => {});
    const rules = await om.listExistentialRules(db);
    expect(rules).toEqual([]);
  });
});

async function putAliasType(db, alias, canonical) {
  const built = dsl.query()
    .input({
      alias: dsl.param('alias', alias),
      canonical: dsl.param('canonical', canonical),
    })
    .put('om_alias_type', ['alias'], ['canonical'])
    .build();
  await db.run(built.script, built.params);
}

describe('OM-024: alias canonicalization on define (T1.2)', () => {
  test('rule defined via alias names is persisted with canonical names', async () => {
    const { db } = await createTestDb();
    await om.defineType(db, 'PurchaseOrder', 'Purchase order');
    await om.defineType(db, 'Shipment', 'Shipment');
    await om.defineRelation(db, 'has_shipment', 'PurchaseOrder', 'Shipment', true);
    // 'Order' is a legacy alias of the canonical 'PurchaseOrder'.
    await putAliasType(db, 'Order', 'PurchaseOrder');

    await om.defineExistentialRule(db, 'r_alias', {
      forEach: { type: 'Order' },
      exists: { rel: 'has_shipment', toType: 'Shipment' },
    });

    const rules = await om.listExistentialRules(db);
    expect(rules[0].spec.forEach.type).toBe('PurchaseOrder');
  });
});
