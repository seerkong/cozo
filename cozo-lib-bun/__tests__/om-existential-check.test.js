const { describe, test, expect } = require('bun:test');

const om = require('../cozo-om');
const { createTestDb } = require('./helpers');

async function createOrderWorld() {
  const { db } = await createTestDb();
  await om.defineType(db, 'Order', 'Order');
  await om.defineType(db, 'RushOrder', 'Rush order', { parentType: 'Order' });
  await om.defineType(db, 'Shipment', 'Shipment');
  await om.defineType(db, 'ExpressShipment', 'Express shipment', { parentType: 'Shipment' });
  await om.defineRelation(db, 'has_shipment', 'Order', 'Shipment', true);
  return db;
}

describe('OM-025: checkExistentialRules basic detection', () => {
  test('reports entity missing the edge, skips satisfied entity', async () => {
    const db = await createOrderWorld();
    await om.defineExistentialRule(db, 'order_must_have_shipment', {
      forEach: { type: 'Order' },
      exists: { rel: 'has_shipment', toType: 'Shipment' },
      message: 'every order needs a shipment',
    });

    await om.createEntity(db, 'o1', 'Order', 'order 1');
    await om.createEntity(db, 'o2', 'Order', 'order 2');
    await om.createEntity(db, 's1', 'Shipment', 'shipment 1');
    await om.linkEntities(db, 'o2', 'has_shipment', 's1');

    const violations = await om.checkExistentialRules(db);
    expect(violations).toEqual([
      { rule: 'order_must_have_shipment', entityId: 'o1', message: 'every order needs a shipment' },
    ]);
  });

  test('polymorphic body and head matching', async () => {
    const db = await createOrderWorld();
    await om.defineExistentialRule(db, 'order_must_have_shipment', {
      forEach: { type: 'Order' },
      exists: { rel: 'has_shipment', toType: 'Shipment' },
    });

    // RushOrder instance satisfied via an ExpressShipment (both are subtypes).
    await om.createEntity(db, 'ro1', 'RushOrder', 'rush order 1');
    await om.createEntity(db, 'es1', 'ExpressShipment', 'express shipment 1');
    await om.linkEntities(db, 'ro1', 'has_shipment', 'es1');
    // RushOrder instance without any shipment.
    await om.createEntity(db, 'ro2', 'RushOrder', 'rush order 2');

    const violations = await om.checkExistentialRules(db);
    expect(violations.map((v) => v.entityId)).toEqual(['ro2']);
  });

  test('disabled rules are skipped and options.rules filters', async () => {
    const db = await createOrderWorld();
    await om.defineExistentialRule(db, 'r_enabled', {
      forEach: { type: 'Order' },
      exists: { rel: 'has_shipment', toType: 'Shipment' },
    });
    await om.defineExistentialRule(db, 'r_disabled', {
      forEach: { type: 'Order' },
      exists: { rel: 'has_shipment', toType: 'Shipment' },
      enabled: false,
    });
    await om.createEntity(db, 'o1', 'Order', 'order 1');

    const all = await om.checkExistentialRules(db);
    expect(all.map((v) => v.rule)).toEqual(['r_enabled']);

    const filtered = await om.checkExistentialRules(db, { rules: ['r_disabled'] });
    expect(filtered).toEqual([]);
  });

  test("direction 'in' requires an incoming edge", async () => {
    const { db } = await createTestDb();
    await om.defineType(db, 'Employee', 'Employee');
    await om.defineType(db, 'Department', 'Department');
    await om.defineRelation(db, 'belongs_to', 'Employee', 'Department', true);
    await om.defineExistentialRule(db, 'dept_must_have_member', {
      forEach: { type: 'Department' },
      exists: { rel: 'belongs_to', direction: 'in', toType: 'Employee' },
    });

    await om.createEntity(db, 'd1', 'Department', 'staffed dept');
    await om.createEntity(db, 'd2', 'Department', 'empty dept');
    await om.createEntity(db, 'e1', 'Employee', 'employee 1');
    await om.linkEntities(db, 'e1', 'belongs_to', 'd1');

    const violations = await om.checkExistentialRules(db);
    expect(violations.map((v) => v.entityId)).toEqual(['d2']);
  });

  test('violations are sorted by rule then entityId', async () => {
    const db = await createOrderWorld();
    await om.defineExistentialRule(db, 'b_rule', {
      forEach: { type: 'Order' },
      exists: { rel: 'has_shipment', toType: 'Shipment' },
    });
    await om.defineExistentialRule(db, 'a_rule', {
      forEach: { type: 'Order' },
      exists: { rel: 'has_shipment', toType: 'Shipment' },
    });
    await om.createEntity(db, 'o2', 'Order', 'order 2');
    await om.createEntity(db, 'o1', 'Order', 'order 1');

    const violations = await om.checkExistentialRules(db);
    expect(violations.map((v) => `${v.rule}:${v.entityId}`)).toEqual([
      'a_rule:o1',
      'a_rule:o2',
      'b_rule:o1',
      'b_rule:o2',
    ]);
  });

  test('edge to wrong target type does not satisfy the rule', async () => {
    const { db } = await createTestDb();
    await om.defineType(db, 'Order', 'Order');
    await om.defineType(db, 'Shipment', 'Shipment');
    await om.defineType(db, 'Document', 'Document');
    // Relation broad enough to accept both target types.
    await om.defineType(db, 'Anything', 'Anything');
    await om.defineRelation(db, 'attached_to', 'Order', 'Anything', true);
    await om.defineType(db, 'AttachableShipment', 'Shipment-as-anything', { parentType: 'Anything' });
    await om.defineType(db, 'AttachableDoc', 'Doc-as-anything', { parentType: 'Anything' });
    await om.defineExistentialRule(db, 'order_must_attach_shipment', {
      forEach: { type: 'Order' },
      exists: { rel: 'attached_to', toType: 'AttachableShipment' },
    });

    await om.createEntity(db, 'o1', 'Order', 'order 1');
    await om.createEntity(db, 'doc1', 'AttachableDoc', 'doc 1');
    await om.linkEntities(db, 'o1', 'attached_to', 'doc1');

    const violations = await om.checkExistentialRules(db);
    expect(violations.map((v) => v.entityId)).toEqual(['o1']);
  });
});
