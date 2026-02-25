const { describe, test, expect } = require('bun:test');

const { CozoDb } = require('../index');
const om = require('../cozo-om');
const { createTestDb } = require('./helpers');

async function createOrderWorld() {
  const { db } = await createTestDb();
  await om.defineType(db, 'Order', 'Order');
  await om.defineType(db, 'Shipment', 'Shipment');
  await om.defineRelation(db, 'has_shipment', 'Order', 'Shipment', true);
  await om.defineExistentialRule(db, 'order_needs_shipment', {
    forEach: { type: 'Order' },
    exists: { rel: 'has_shipment', toType: 'Shipment' },
    mode: 'materialize',
  });
  return db;
}

describe('OM-026: chase loop — fixpoint, idempotency, cap (T3.2)', () => {
  test('rerun is idempotent: no duplicates, empty created, fixpoint', async () => {
    const db = await createOrderWorld();
    await om.createEntity(db, 'o1', 'Order', 'order 1');

    const first = await om.applyExistentialRules(db);
    expect(first.created.length).toBe(1);
    const skolemId = first.created[0].skolemId;

    const second = await om.applyExistentialRules(db);
    expect(second.created).toEqual([]);
    expect(second.reachedFixpoint).toBe(true);

    // Deterministic Skolem id: only one Shipment entity exists.
    const shipments = await om.findByType(db, 'Shipment');
    expect(shipments.map((s) => s.id)).toEqual([skolemId]);
  });

  test('manually fixed entities are not materialized', async () => {
    const db = await createOrderWorld();
    await om.createEntity(db, 'o_fixed', 'Order', 'manually fixed');
    await om.createEntity(db, 'o_broken', 'Order', 'needs chase');
    await om.createEntity(db, 's_real', 'Shipment', 'real shipment');
    await om.linkEntities(db, 'o_fixed', 'has_shipment', 's_real');

    const result = await om.applyExistentialRules(db);
    expect(result.created.map((c) => c.triggerEntityId)).toEqual(['o_broken']);
  });

  test('cyclic rules stop at maxIterations with diagnostics', async () => {
    const { db } = await createTestDb();
    await om.defineType(db, 'TypeA', 'A');
    await om.defineType(db, 'TypeB', 'B');
    await om.defineRelation(db, 'a_to_b', 'TypeA', 'TypeB', true);
    await om.defineRelation(db, 'b_to_a', 'TypeB', 'TypeA', true);
    await om.defineExistentialRule(db, 'a_needs_b', {
      forEach: { type: 'TypeA' },
      exists: { rel: 'a_to_b', toType: 'TypeB' },
      mode: 'materialize',
    });
    await om.defineExistentialRule(db, 'b_needs_a', {
      forEach: { type: 'TypeB' },
      exists: { rel: 'b_to_a', toType: 'TypeA' },
      mode: 'materialize',
    });
    await om.createEntity(db, 'a1', 'TypeA', 'seed');

    const result = await om.applyExistentialRules(db, { maxIterations: 3 });
    expect(result.iterations).toBe(3);
    expect(result.reachedFixpoint).toBe(false);
    expect(result.diagnostics.length).toBeGreaterThan(0);
    expect(result.diagnostics[0]).toHaveProperty('ruleName');
    expect(result.diagnostics[0]).toHaveProperty('remainingViolations');
  });

  test('validTime is applied to materialized edges and properties', async () => {
    const db = await createOrderWorld();
    await om.createEntity(db, 'o1', 'Order', 'order 1');

    const result = await om.applyExistentialRules(db, { validTime: '2026-01-01T00:00:00Z' });
    const skolemId = result.created[0].skolemId;

    const before = await om.getNeighborsAsOf(db, 'o1', 'has_shipment', '2025-06-01T00:00:00Z');
    expect(before.outgoing).toEqual([]);

    const after = await om.getNeighborsAsOf(db, 'o1', 'has_shipment', '2026-06-01T00:00:00Z');
    expect(after.outgoing.map((n) => n.entityId)).toEqual([skolemId]);
  });

  test('invalid maxIterations is rejected', async () => {
    const db = await createOrderWorld();
    await expect(om.applyExistentialRules(db, { maxIterations: 0 })).rejects.toThrow(/maxIterations/);
  });

  test('colliding rule/entity name concatenations do not shadow each other', async () => {
    // ('r1' + '2x') and ('r12' + 'x') concatenate to the same string; the
    // per-call dedup key must keep them apart so both violations are
    // materialized in a single apply call.
    const { db } = await createTestDb();
    await om.defineType(db, 'TypeA', 'A');
    await om.defineType(db, 'TypeB', 'B');
    await om.defineType(db, 'TargetA', 'Target A');
    await om.defineType(db, 'TargetB', 'Target B');
    await om.defineRelation(db, 'rel_a', 'TypeA', 'TargetA', true);
    await om.defineRelation(db, 'rel_b', 'TypeB', 'TargetB', true);
    await om.defineExistentialRule(db, 'r1', {
      forEach: { type: 'TypeA' },
      exists: { rel: 'rel_a', toType: 'TargetA' },
      mode: 'materialize',
    });
    await om.defineExistentialRule(db, 'r12', {
      forEach: { type: 'TypeB' },
      exists: { rel: 'rel_b', toType: 'TargetB' },
      mode: 'materialize',
    });
    await om.createEntity(db, '2x', 'TypeA', 'entity 2x');
    await om.createEntity(db, 'x', 'TypeB', 'entity x');

    const result = await om.applyExistentialRules(db);
    const createdPairs = result.created.map((c) => `${c.rule}|${c.triggerEntityId}`).sort();
    expect(createdPairs).toEqual(['r1|2x', 'r12|x'].sort());
    expect(result.reachedFixpoint).toBe(true);
    expect(await om.checkExistentialRules(db)).toEqual([]);
  });

  test('check and apply on a db without initSchema are safe no-ops', async () => {
    const db = new CozoDb('mem', '', {});
    expect(await om.checkExistentialRules(db)).toEqual([]);
    const result = await om.applyExistentialRules(db);
    expect(result.created).toEqual([]);
    expect(result.reachedFixpoint).toBe(true);
  });
});
