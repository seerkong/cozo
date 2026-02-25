const { CozoDb, om, dsl } = require('.');

async function seedOntology(db) {
  await om.initSchema(db);

  await om.defineType(db, 'User', 'Person who can own projects and receive tasks');
  await om.defineType(db, 'Project', 'Container of tasks');
  await om.defineType(db, 'Task', 'Work item under a project');

  await om.defineAttribute(db, 'User', 'email', 'String', true);
  await om.defineAttribute(db, 'User', 'role', 'String', false);
  await om.defineAttribute(db, 'Project', 'status', 'String', true);
  await om.defineAttribute(db, 'Task', 'priority', 'String', true);
  await om.defineAttribute(db, 'Task', 'estimate_hours', 'Number', false);

  await om.defineRelation(db, 'owns', 'User', 'Project', true);
  await om.defineRelation(db, 'contains', 'Project', 'Task', true);
  await om.defineRelation(db, 'assigned_to', 'Task', 'User', true);

  await om.ingestBatch(db, {
    entities: [
      { id: 'u:alice', typeName: 'User', label: 'Alice' },
      { id: 'u:bob', typeName: 'User', label: 'Bob' },
      { id: 'u:carol', typeName: 'User', label: 'Carol' },
      { id: 'p:apollo', typeName: 'Project', label: 'Apollo' },
      { id: 'p:hera', typeName: 'Project', label: 'Hera' },
      { id: 't:api', typeName: 'Task', label: 'Build API' },
      { id: 't:web', typeName: 'Task', label: 'Build Web UI' },
      { id: 't:data', typeName: 'Task', label: 'Build Data Pipeline' },
    ],
    properties: [
      { entityId: 'u:alice', attrName: 'email', value: 'alice@example.com' },
      { entityId: 'u:alice', attrName: 'role', value: 'manager' },
      { entityId: 'u:bob', attrName: 'email', value: 'bob@example.com' },
      { entityId: 'u:bob', attrName: 'role', value: 'engineer' },
      { entityId: 'u:carol', attrName: 'email', value: 'carol@example.com' },
      { entityId: 'u:carol', attrName: 'role', value: 'engineer' },
      { entityId: 'p:apollo', attrName: 'status', value: 'active' },
      { entityId: 'p:hera', attrName: 'status', value: 'active' },
      { entityId: 't:api', attrName: 'priority', value: 'high' },
      { entityId: 't:api', attrName: 'estimate_hours', value: 16 },
      { entityId: 't:web', attrName: 'priority', value: 'medium' },
      { entityId: 't:web', attrName: 'estimate_hours', value: 24 },
      { entityId: 't:data', attrName: 'priority', value: 'high' },
      { entityId: 't:data', attrName: 'estimate_hours', value: 20 },
    ],
    edges: [
      { fromId: 'u:alice', relName: 'owns', toId: 'p:apollo', props: { since: '2026-01-01' } },
      { fromId: 'u:carol', relName: 'owns', toId: 'p:hera', props: { since: '2026-02-01' } },
      { fromId: 'p:apollo', relName: 'contains', toId: 't:api', props: { order: 1 } },
      { fromId: 'p:apollo', relName: 'contains', toId: 't:web', props: { order: 2 } },
      { fromId: 'p:hera', relName: 'contains', toId: 't:data', props: { order: 1 } },
      { fromId: 't:api', relName: 'assigned_to', toId: 'u:bob', props: { effort: 0.8 } },
      { fromId: 't:web', relName: 'assigned_to', toId: 'u:alice', props: { effort: 0.2 } },
      { fromId: 't:data', relName: 'assigned_to', toId: 'u:carol', props: { effort: 1.0 } },
    ],
  });
}

async function showAnalytics(db) {
  const aliceTasks = await om.traverse(db, 'u:alice', ['owns', 'contains']);
  const bobNeighbors = await om.getNeighbors(db, 'u:bob', 'assigned_to');
  const engineers = await om.findByType(db, 'User', { role: 'engineer' });
  const totalTaskHours = await om.aggregateByType(db, 'Task', 'estimate_hours', 'sum');
  const avgTaskHours = await om.aggregateByType(db, 'Task', 'estimate_hours', 'avg');
  const aliceView = await om.getEntityView(db, 'u:alice');

  const dslTaskQuery = dsl
    .query()
    .select(['id', 'label', 'estimate'])
    .fromStored('om_entity', {
      id: dsl.var('id'),
      type_name: dsl.param('task_type', 'Task'),
      label: dsl.var('label'),
    })
    .fromStored('om_property', {
      entity_id: dsl.var('id'),
      attr_name: dsl.param('estimate_attr', 'estimate_hours'),
      value: dsl.var('estimate'),
    })
    .where(
      dsl.and(
        dsl.gt(dsl.var('estimate'), dsl.param('min_hours', 15)),
        dsl.in(dsl.var('id'), [
          dsl.param('task_api', 't:api'),
          dsl.param('task_web', 't:web'),
          dsl.param('task_data', 't:data'),
        ]),
        dsl.not(dsl.lt(dsl.var('estimate'), dsl.param('min_non_negative', 0)))
      )
    )
    .order('estimate')
    .build();

  const dslTaskRows = await db.run(dslTaskQuery.script, dslTaskQuery.params);

  const impact = await om.impactAnalysis(db, {
    rootId: 'u:alice',
    relNames: ['owns', 'contains'],
    maxDepth: 3,
    direction: 'outgoing',
  });
  const ownership = await om.ownershipTree(db, {
    rootId: 'u:alice',
    ownerRelNames: ['owns', 'contains'],
    maxDepth: 3,
  });
  const hotspots = await om.riskHotspot(db, {
    typeName: 'Task',
    riskAttr: 'estimate_hours',
    topK: 3,
    minScore: 0,
    degreeWeight: 1,
  });

  console.log('--- Reachable tasks from Alice ownership path ---');
  console.log(aliceTasks);

  console.log('--- Bob neighbors on assigned_to ---');
  console.log(bobNeighbors);

  console.log('--- Users filtered by role=engineer ---');
  console.log(engineers);

  console.log('--- Task estimate aggregation ---');
  console.log({ totalTaskHours, avgTaskHours });

  console.log('--- Entity view: Alice ---');
  console.log(JSON.stringify(aliceView, null, 2));

  console.log('--- DSL v2 filtered task rows ---');
  console.log(dslTaskRows.rows);

  console.log('--- Template: impactAnalysis ---');
  console.log(impact.stats);
  console.log(impact.data.visual);

  console.log('--- Template: ownershipTree ---');
  console.log(ownership.stats);
  console.log(ownership.data.visual);

  console.log('--- Template: riskHotspot ---');
  console.log(hotspots.data.hotspots);
  console.log(hotspots.data.visual);
}

async function showValidationDemos(db) {
  console.log('--- Constraint validation demos ---');

  try {
    await om.setProperty(db, 't:api', 'priority', 999);
    console.log('[UNEXPECTED] Type mismatch check failed to reject input');
  } catch (error) {
    console.log(`[EXPECTED] ${error.message}`);
  }

  try {
    await om.setProperty(db, 'u:alice', 'age', 30);
    console.log('[UNEXPECTED] Undefined attribute check failed to reject input');
  } catch (error) {
    console.log(`[EXPECTED] ${error.message}`);
  }

  try {
    await om.linkEntities(db, 't:api', 'owns', 'u:alice');
    console.log('[UNEXPECTED] Relation type check failed to reject input');
  } catch (error) {
    console.log(`[EXPECTED] ${error.message}`);
  }

  try {
    await om.linkEntities(db, 'u:alice', 'manages', 'u:bob');
    console.log('[UNEXPECTED] Undefined relation check failed to reject input');
  } catch (error) {
    console.log(`[EXPECTED] ${error.message}`);
  }

  await om.createEntity(db, 't:orphan', 'Task', 'Orphan Task');
  const orphanValidation = await om.validateEntity(db, 't:orphan');
  console.log('--- Orphan validation report ---');
  console.log(orphanValidation);

  try {
    await om.finalizeEntity(db, 't:orphan');
    console.log('[UNEXPECTED] Required property check failed to reject input');
  } catch (error) {
    console.log(`[EXPECTED] ${error.message}`);
  }

  await om.setProperty(db, 't:orphan', 'priority', 'low');
  await om.finalizeEntity(db, 't:orphan');
  console.log('[OK] Required property check passed after filling priority');
}

async function main() {
  const db = new CozoDb();
  try {
    await seedOntology(db);
    await showAnalytics(db);
    await showValidationDemos(db);
  } finally {
    db.close();
  }
}

main().catch((e) => {
  console.error(e.display || e.message || e);
  process.exitCode = 1;
});
