const { describe, test, expect } = require('bun:test');

const om = require('../cozo-om');
const { createTestDb } = require('./helpers');

async function createOrderWorld() {
  const { db } = await createTestDb();
  await om.defineType(db, 'Order', 'Order');
  await om.defineType(db, 'Shipment', 'Shipment');
  await om.defineRelation(db, 'has_shipment', 'Order', 'Shipment', true);
  await om.defineAttribute(db, 'Order', 'status', 'String', false);
  await om.defineAttribute(db, 'Order', 'amount', 'Number', false);
  return db;
}

describe('OM-025: where conditions filter the rule body', () => {
  test('only entities matching where enter the violation set', async () => {
    const db = await createOrderWorld();
    await om.defineExistentialRule(db, 'confirmed_order_needs_shipment', {
      forEach: { type: 'Order', where: [{ attr: 'status', op: '=', value: 'confirmed' }] },
      exists: { rel: 'has_shipment', toType: 'Shipment' },
    });

    await om.createEntity(db, 'o_draft', 'Order', 'draft order');
    await om.setProperty(db, 'o_draft', 'status', 'draft');
    await om.createEntity(db, 'o_conf', 'Order', 'confirmed order');
    await om.setProperty(db, 'o_conf', 'status', 'confirmed');
    // Order without any status property at all is also outside the body.
    await om.createEntity(db, 'o_nostatus', 'Order', 'no status');

    const violations = await om.checkExistentialRules(db);
    expect(violations.map((v) => v.entityId)).toEqual(['o_conf']);
  });

  test('numeric comparison ops work in where', async () => {
    const db = await createOrderWorld();
    await om.defineExistentialRule(db, 'big_order_needs_shipment', {
      forEach: { type: 'Order', where: [{ attr: 'amount', op: '>=', value: 1000 }] },
      exists: { rel: 'has_shipment', toType: 'Shipment' },
    });

    await om.createEntity(db, 'o_small', 'Order', 'small');
    await om.setProperty(db, 'o_small', 'amount', 10);
    await om.createEntity(db, 'o_big', 'Order', 'big');
    await om.setProperty(db, 'o_big', 'amount', 5000);

    const violations = await om.checkExistentialRules(db);
    expect(violations.map((v) => v.entityId)).toEqual(['o_big']);
  });
});

describe('OM-025: temporal semantics (@NOW and asOf)', () => {
  test('retracted edge counts as missing at NOW', async () => {
    const db = await createOrderWorld();
    await om.defineExistentialRule(db, 'order_needs_shipment', {
      forEach: { type: 'Order' },
      exists: { rel: 'has_shipment', toType: 'Shipment' },
    });

    await om.createEntity(db, 'o1', 'Order', 'order 1');
    await om.createEntity(db, 's1', 'Shipment', 'shipment 1');
    await om.linkEntities(db, 'o1', 'has_shipment', 's1', {}, { validTime: '2026-01-01T00:00:00Z' });

    expect(await om.checkExistentialRules(db)).toEqual([]);

    await om.unlinkEntities(db, 'o1', 'has_shipment', 's1', { validTime: '2026-03-01T00:00:00Z' });

    const violations = await om.checkExistentialRules(db);
    expect(violations.map((v) => v.entityId)).toEqual(['o1']);

    // The edge was still valid between link and unlink.
    const during = await om.checkExistentialRules(db, { asOf: '2026-02-01T00:00:00Z' });
    expect(during).toEqual([]);
  });

  test('asOf reports historical violations before the edge existed', async () => {
    const db = await createOrderWorld();
    await om.defineExistentialRule(db, 'order_needs_shipment', {
      forEach: { type: 'Order' },
      exists: { rel: 'has_shipment', toType: 'Shipment' },
    });

    await om.createEntity(db, 'o1', 'Order', 'order 1');
    await om.createEntity(db, 's1', 'Shipment', 'shipment 1');
    await om.linkEntities(db, 'o1', 'has_shipment', 's1', {}, { validTime: '2026-01-01T00:00:00Z' });

    const before = await om.checkExistentialRules(db, { asOf: '2025-06-01T00:00:00Z' });
    expect(before.map((v) => v.entityId)).toEqual(['o1']);

    const after = await om.checkExistentialRules(db, { asOf: '2026-06-01T00:00:00Z' });
    expect(after).toEqual([]);

    expect(await om.checkExistentialRules(db)).toEqual([]);
  });

  test('where conditions are also evaluated as-of the requested time', async () => {
    const db = await createOrderWorld();
    await om.defineExistentialRule(db, 'confirmed_order_needs_shipment', {
      forEach: { type: 'Order', where: [{ attr: 'status', op: '=', value: 'confirmed' }] },
      exists: { rel: 'has_shipment', toType: 'Shipment' },
    });

    await om.createEntity(db, 'o1', 'Order', 'order 1');
    // Confirmed only from 2026-02 onwards.
    await om.setProperty(db, 'o1', 'status', 'draft', { validTime: '2026-01-01T00:00:00Z' });
    await om.setProperty(db, 'o1', 'status', 'confirmed', { validTime: '2026-02-01T00:00:00Z' });

    const before = await om.checkExistentialRules(db, { asOf: '2026-01-15T00:00:00Z' });
    expect(before).toEqual([]);

    const after = await om.checkExistentialRules(db, { asOf: '2026-02-15T00:00:00Z' });
    expect(after.map((v) => v.entityId)).toEqual(['o1']);
  });

  test('asOf rejects invalid timestamps', async () => {
    const db = await createOrderWorld();
    await expect(om.checkExistentialRules(db, { asOf: 'not-a-time' })).rejects.toThrow(/asOf/);
  });
});
