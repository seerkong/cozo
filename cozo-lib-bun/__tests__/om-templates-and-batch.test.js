const { expect, test, describe, beforeAll, afterAll } = require('bun:test');
const { createTestDb } = require('./helpers');

let db;
let om;

beforeAll(async () => {
  ({ db, om } = await createTestDb());
});

afterAll(() => {
  db.close();
});

// ---------------------------------------------------------------------------
// 1) impactAnalysis basic graph
// ---------------------------------------------------------------------------
describe('impactAnalysis', () => {
  beforeAll(async () => {
    await om.defineType(db, 'TypeA', 'Type A');
    await om.defineType(db, 'TypeB', 'Type B');
    await om.defineRelation(db, 'r', 'TypeA', 'TypeB', true);

    await om.createEntity(db, 'A1', 'TypeA', 'A1');
    await om.createEntity(db, 'B1', 'TypeB', 'B1');
    await om.linkEntities(db, 'A1', 'r', 'B1');
  });

  test('returns impactedCount=1 and visual graph with nodes and edges', async () => {
    const result = await om.impactAnalysis(db, {
      rootId: 'A1',
      relNames: ['r'],
      maxDepth: 2,
      direction: 'outgoing',
    });

    expect(result.template).toBe('impactAnalysis');
    expect(result.stats.impactedCount).toBe(1);
    expect(result.data.visual.graph.nodes.length).toBeGreaterThanOrEqual(2);
    expect(result.data.visual.graph.edges.length).toBeGreaterThanOrEqual(1);

    // root node flagged
    const rootNode = result.data.visual.graph.nodes.find((n) => n.id === 'A1');
    expect(rootNode.flags.isRoot).toBe(true);
  });
});

// ---------------------------------------------------------------------------
// 2) ownershipTree
// ---------------------------------------------------------------------------
describe('ownershipTree', () => {
  beforeAll(async () => {
    await om.defineType(db, 'Container', 'Container');
    await om.defineRelation(db, 'contains', 'Container', 'Container', true);

    await om.createEntity(db, 'Aroot', 'Container', 'Aroot');
    await om.createEntity(db, 'Achild', 'Container', 'Achild');
    await om.linkEntities(db, 'Aroot', 'contains', 'Achild');
  });

  test('returns tree with rootId and childrenById entries', async () => {
    const result = await om.ownershipTree(db, {
      rootId: 'Aroot',
      ownerRelNames: ['contains'],
      maxDepth: 2,
    });

    expect(result.template).toBe('ownershipTree');
    expect(result.data.visual.tree.rootId).toBe('Aroot');

    const children = result.data.visual.tree.childrenById;
    expect(children).toBeDefined();
    expect(Object.keys(children).length).toBeGreaterThanOrEqual(1);
    expect(children['Aroot'].length).toBe(1);
    expect(children['Aroot'][0].toId).toBe('Achild');
  });
});

// ---------------------------------------------------------------------------
// 3) riskHotspot
// ---------------------------------------------------------------------------
describe('riskHotspot', () => {
  beforeAll(async () => {
    await om.defineType(db, 'Task', 'Task');
    await om.defineAttribute(db, 'Task', 'estimate_hours', 'Number', false);
    await om.defineRelation(db, 'depends_on', 'Task', 'Task', true);

    await om.createEntity(db, 't1', 'Task', 'Task 1');
    await om.setProperty(db, 't1', 'estimate_hours', 10);

    await om.createEntity(db, 't2', 'Task', 'Task 2');
    await om.setProperty(db, 't2', 'estimate_hours', 50);

    await om.createEntity(db, 't3', 'Task', 'Task 3');
    await om.setProperty(db, 't3', 'estimate_hours', 30);

    // t1 -> t2 and t1 -> t3 give t1 degree 2, t2 degree 1, t3 degree 1
    await om.linkEntities(db, 't1', 'depends_on', 't2');
    await om.linkEntities(db, 't1', 'depends_on', 't3');
  });

  test('returns topK=2 sorted descending by score', async () => {
    const result = await om.riskHotspot(db, {
      typeName: 'Task',
      riskAttr: 'estimate_hours',
      topK: 2,
      degreeWeight: 1,
    });

    expect(result.template).toBe('riskHotspot');
    expect(result.stats.returnedCount).toBe(2);

    const ranking = result.data.visual.ranking;
    expect(ranking.length).toBe(2);
    // scores must be descending
    expect(ranking[0].score).toBeGreaterThanOrEqual(ranking[1].score);

    // t2 has score 50 + 1*1 = 51, t3 has score 30 + 1*1 = 31
    // t1 has score 10 + 1*2 = 12 -- so top 2 are t2(51) and t3(31)
    expect(ranking[0].id).toBe('t2');
    expect(ranking[1].id).toBe('t3');
  });
});

// ---------------------------------------------------------------------------
// 4) ingestBatch rollback on failure
// ---------------------------------------------------------------------------
describe('ingestBatch rollback', () => {
  beforeAll(async () => {
    await om.defineType(db, 'TypeX', 'TypeX');
    await om.defineAttribute(db, 'TypeX', 'n', 'Number', true);
  });

  test('throws on type mismatch and rolls back entity creation', async () => {
    const batch = {
      entities: [{ id: 'x1', typeName: 'TypeX', label: 'X1' }],
      properties: [{ entityId: 'x1', attrName: 'n', value: 'not a number' }],
      edges: [],
    };

    await expect(om.ingestBatch(db, batch)).rejects.toThrow();

    // entity x1 should not exist after rollback
    const rows = await db.run(
      '?[id] := *om_entity{id, type_name, label}, id = $id',
      { id: 'x1' }
    );
    expect(rows.rows.length).toBe(0);
  });
});

// ---------------------------------------------------------------------------
// 5) traverse
// ---------------------------------------------------------------------------
describe('traverse', () => {
  test('traverse with empty relPath returns start entity', async () => {
    const result = await om.traverse(db, 'A1', []);
    expect(result.length).toBe(1);
    expect(result[0].id).toBe('A1');
    expect(result[0].typeName).toBe('TypeA');
  });

  test('traverse with path length 1 follows one hop', async () => {
    const result = await om.traverse(db, 'A1', ['r']);
    expect(result.length).toBe(1);
    expect(result[0].id).toBe('B1');
    expect(result[0].typeName).toBe('TypeB');
  });
});
