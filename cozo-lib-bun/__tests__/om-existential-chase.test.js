const { describe, test, expect } = require('bun:test');

const om = require('../cozo-om');
const { createTestDb } = require('./helpers');

async function createOrderWorld() {
  const { db } = await createTestDb();
  await om.defineType(db, 'Order', 'Order');
  await om.defineType(db, 'Shipment', 'Shipment');
  await om.defineRelation(db, 'has_shipment', 'Order', 'Shipment', true);
  await om.defineAttribute(db, 'Shipment', 'status', 'String', false);
  return db;
}

describe('OM-026: applyExistentialRules single-round materialization (T3.1)', () => {
  test('materializes a Skolem entity and edge, clearing the violation', async () => {
    const db = await createOrderWorld();
    await om.defineExistentialRule(db, 'order_needs_shipment', {
      forEach: { type: 'Order' },
      exists: { rel: 'has_shipment', toType: 'Shipment' },
      mode: 'materialize',
      materialize: { labelTemplate: 'auto shipment for {fromId}', props: { status: 'pending' } },
    });
    await om.createEntity(db, 'o1', 'Order', 'order 1');

    const result = await om.applyExistentialRules(db);

    expect(result.reachedFixpoint).toBe(true);
    expect(result.created.length).toBe(1);
    const entry = result.created[0];
    expect(entry.rule).toBe('order_needs_shipment');
    expect(entry.triggerEntityId).toBe('o1');
    expect(entry.skolemId.startsWith('skolem:')).toBe(true);

    // Violation is gone and the Skolem entity is a real, typed entity.
    expect(await om.checkExistentialRules(db)).toEqual([]);
    expect(await om.getEntityType(db, entry.skolemId)).toBe('Shipment');

    const view = await om.getEntityView(db, entry.skolemId);
    expect(view.label).toBe('auto shipment for o1');
    expect(view.properties.status).toBe('pending');
  });

  test('Skolem entity is marked with the origin rule via _skolem_rule', async () => {
    const db = await createOrderWorld();
    await om.defineExistentialRule(db, 'order_needs_shipment', {
      forEach: { type: 'Order' },
      exists: { rel: 'has_shipment', toType: 'Shipment' },
      mode: 'materialize',
    });
    await om.createEntity(db, 'o1', 'Order', 'order 1');

    const result = await om.applyExistentialRules(db);
    const skolemId = result.created[0].skolemId;
    expect(await om.getProperty(db, skolemId, '_skolem_rule')).toBe('order_needs_shipment');
  });

  test('check-mode rules are not materialized', async () => {
    const db = await createOrderWorld();
    await om.defineExistentialRule(db, 'check_only', {
      forEach: { type: 'Order' },
      exists: { rel: 'has_shipment', toType: 'Shipment' },
      mode: 'check',
    });
    await om.createEntity(db, 'o1', 'Order', 'order 1');

    const result = await om.applyExistentialRules(db);
    expect(result.created).toEqual([]);
    expect(result.reachedFixpoint).toBe(true);

    // The check-mode violation is untouched.
    const violations = await om.checkExistentialRules(db);
    expect(violations.map((v) => v.entityId)).toEqual(['o1']);
  });

  test("direction 'in' materializes the Skolem entity on the from side", async () => {
    const { db } = await createTestDb();
    await om.defineType(db, 'Employee', 'Employee');
    await om.defineType(db, 'Department', 'Department');
    await om.defineRelation(db, 'belongs_to', 'Employee', 'Department', true);
    await om.defineExistentialRule(db, 'dept_needs_member', {
      forEach: { type: 'Department' },
      exists: { rel: 'belongs_to', direction: 'in', toType: 'Employee' },
      mode: 'materialize',
    });
    await om.createEntity(db, 'd1', 'Department', 'dept 1');

    const result = await om.applyExistentialRules(db);
    expect(result.created.length).toBe(1);
    const skolemId = result.created[0].skolemId;
    expect(await om.getEntityType(db, skolemId)).toBe('Employee');

    const neighbors = await om.getNeighbors(db, 'd1', 'belongs_to', 'incoming');
    expect(neighbors.incoming.map((n) => n.entityId)).toEqual([skolemId]);
  });

  test('options.rules restricts which materialize rules run', async () => {
    const db = await createOrderWorld();
    await om.defineType(db, 'Invoice', 'Invoice');
    await om.defineRelation(db, 'has_invoice', 'Order', 'Invoice', true);
    await om.defineExistentialRule(db, 'needs_shipment', {
      forEach: { type: 'Order' },
      exists: { rel: 'has_shipment', toType: 'Shipment' },
      mode: 'materialize',
    });
    await om.defineExistentialRule(db, 'needs_invoice', {
      forEach: { type: 'Order' },
      exists: { rel: 'has_invoice', toType: 'Invoice' },
      mode: 'materialize',
    });
    await om.createEntity(db, 'o1', 'Order', 'order 1');

    const result = await om.applyExistentialRules(db, { rules: ['needs_shipment'] });
    expect(result.created.map((c) => c.rule)).toEqual(['needs_shipment']);

    const remaining = await om.checkExistentialRules(db);
    expect(remaining.map((v) => v.rule)).toEqual(['needs_invoice']);
  });
});
