const { describe, expect, test } = require('bun:test');
const { createHash } = require('crypto');

const dsl = require('../cozo-dsl');
const { createTestDb } = require('./helpers');

const BEHAVIOR_RELATIONS = [
  'om_constraint_def',
  'om_computed_def',
  'om_action_def',
  'om_mutation_def',
  'om_interceptor_def',
  'om_behavior_binding',
];

async function putBinding(db, {
  kind,
  owner,
  name,
  slot,
  phase = '',
  seq = -1,
  bindingId,
}) {
  await db.run(
    `
?[behavior_kind, owner_type, behavior_name, callback_slot, phase, seq, binding_id] <-
  [[$kind, $owner, $name, $slot, $phase, $seq, $binding_id]]
:put om_behavior_binding {
  behavior_kind, owner_type, behavior_name, callback_slot, phase, seq => binding_id
}
    `.trim(),
    { kind, owner, name, slot, phase, seq, binding_id: bindingId }
  );
}

async function putInterceptor(db, {
  owner,
  action,
  phase,
  seq,
  description,
}) {
  await db.run(
    `
?[type_name, action_name, phase, seq, description] <-
  [[$owner, $action, $phase, $seq, $description]]
:put om_interceptor_def {
  type_name, action_name, phase, seq => description
}
    `.trim(),
    { owner, action, phase, seq, description }
  );
}

async function putActionDefinition(db, owner, action, description) {
  await db.run(
    `
?[type_name, action_name, description] <-
  [[$owner, $action, $description]]
:put om_action_def { type_name, action_name => description }
    `.trim(),
    { owner, action, description }
  );
}

async function putAliasType(db, alias, canonical) {
  const built = dsl
    .query()
    .input({
      alias: dsl.param('alias', alias),
      canonical: dsl.param('canonical', canonical),
    })
    .put('om_alias_type', ['alias'], ['canonical'])
    .build();
  await db.run(built.script, built.params);
}

function behaviorSnapshot(overrides = {}) {
  return {
    formatVersion: 1,
    om_constraint_def: [],
    om_computed_def: [],
    om_action_def: [],
    om_mutation_def: [],
    om_interceptor_def: [],
    om_behavior_binding: [],
    ...overrides,
  };
}

function cloneJson(value) {
  return JSON.parse(JSON.stringify(value));
}

function wrappingDb(db, hooks = {}) {
  let transactionNumber = 0;
  return {
    async run(script, params) {
      if (hooks.beforeDirectRun) await hooks.beforeDirectRun(script, params);
      return db.run(script, params);
    },
    multiTransact(write) {
      transactionNumber++;
      const current = transactionNumber;
      const tx = db.multiTransact(write);
      return {
        async run(script, params) {
          if (hooks.beforeRun) await hooks.beforeRun(current, script, params);
          return tx.run(script, params);
        },
        commit() {
          if (hooks.beforeCommit) hooks.beforeCommit(current);
          tx.commit();
          if (hooks.afterCommit) hooks.afterCommit(current);
        },
        abort() {
          tx.abort();
        },
      };
    },
  };
}

async function captureRejected(promise) {
  try {
    await promise;
  } catch (error) {
    return error;
  }
  throw new Error('Expected operation to reject');
}

async function putStoredSnapshot(db, snapshot) {
  const built = dsl
    .query()
    .input({
      version: dsl.param('version', snapshot.version),
      snapshot_json: dsl.param('snapshot_json', JSON.stringify(snapshot)),
    })
    .put('om_schema_snapshot', ['version'], ['snapshot_json'])
    .build();
  await db.run(built.script, built.params);
}

function schemaChecksum(snapshot) {
  return createHash('sha256')
    .update(JSON.stringify(snapshot), 'utf8')
    .digest('hex');
}

function expectBehaviorSectionKeys(behavior) {
  expect([...Object.keys(behavior)].sort()).toEqual(
    ['formatVersion', ...BEHAVIOR_RELATIONS].sort()
  );
}

async function readStoredSchemaVersionChecksum(db, version) {
  const built = dsl
    .query()
    .select(['checksum'])
    .fromStored('om_schema_version', {
      version: dsl.param('version', version),
      created_at: dsl.var('_created_at'),
      label: dsl.var('_label'),
      description: dsl.var('_description'),
      parent_version: dsl.var('_parent_version'),
      checksum: dsl.var('checksum'),
    })
    .limit(1)
    .build();
  const result = await db.run(built.script, built.params);
  const rows = Array.isArray(result?.rows) ? result.rows : [];
  return rows.length ? rows[0][0] : null;
}

async function expectTargetChecksumMatchesStoredStateAndVersion(db, om, targetSnapshot) {
  const expectedChecksum = schemaChecksum(targetSnapshot);
  const versionChecksum = await readStoredSchemaVersionChecksum(
    db,
    targetSnapshot.version
  );
  const state = await om.getSchemaState(db);

  expect(versionChecksum).toBe(expectedChecksum);
  expect(state.checksum).toBe(expectedChecksum);
  expect(String(expectedChecksum || '')).toMatch(/^[0-9a-f]{64}$/);
  return expectedChecksum;
}

async function readStoredMigrationStatus(db, migrationId) {
  const built = dsl
    .query()
    .select(['status'])
    .fromStored('om_schema_migration', {
      migration_id: dsl.param('migration_id', migrationId),
      from_version: dsl.var('_from_version'),
      to_version: dsl.var('_to_version'),
      applied_at: dsl.var('_applied_at'),
      applied_by: dsl.var('_applied_by'),
      status: dsl.var('status'),
      error: dsl.var('_error'),
      summary_json: dsl.var('_summary_json'),
    })
    .limit(1)
    .build();
  const result = await db.run(built.script, built.params);
  const rows = Array.isArray(result?.rows) ? result.rows : [];
  return rows.length ? rows[0][0] : null;
}

async function typeExists(db, typeName) {
  const result = await db.run(
    '?[present] := *om_type{name: $type_name, description: _description, parent_type: _parent_type}, present = true\n:limit 1',
    { type_name: typeName }
  );
  return Array.isArray(result?.rows) && result.rows.length === 1;
}

async function readBehaviorRelationRows(db, relationName) {
  const columnsByRelation = {
    om_constraint_def: ['type_name', 'constraint_name', 'constraint_type', 'message'],
    om_computed_def: ['type_name', 'attr_name', 'description'],
    om_action_def: ['type_name', 'action_name', 'description'],
    om_mutation_def: ['type_name', 'mutation_name', 'description'],
    om_interceptor_def: ['type_name', 'action_name', 'phase', 'seq', 'description'],
    om_behavior_binding: [
      'behavior_kind',
      'owner_type',
      'behavior_name',
      'callback_slot',
      'phase',
      'seq',
      'binding_id',
    ],
  };
  const columns = columnsByRelation[relationName];
  if (!columns) throw new Error(`Unknown behavior relation ${relationName}`);

  const selectVars = columns.join(', ');
  const storedFields = columns
    .map((column) => `${column}: ${column}`)
    .join(', ');
  const result = await db.run(
    `?[${selectVars}] := *${relationName}{${storedFields}}\n:order ${selectVars}`
  );
  return Array.isArray(result?.rows) ? result.rows : [];
}

async function readBehaviorSnapshotRows(db) {
  const behavior = { formatVersion: 1 };
  for (const relation of BEHAVIOR_RELATIONS) {
    behavior[relation] = await readBehaviorRelationRows(db, relation);
  }
  return behavior;
}

async function replaceBehaviorSnapshotRows(db, behavior) {
  const schemasByRelation = {
    om_constraint_def: {
      head: ['type_name', 'constraint_name', 'constraint_type', 'message'],
      schema: 'type_name, constraint_name => constraint_type, message',
    },
    om_computed_def: {
      head: ['type_name', 'attr_name', 'description'],
      schema: 'type_name, attr_name => description',
    },
    om_action_def: {
      head: ['type_name', 'action_name', 'description'],
      schema: 'type_name, action_name => description',
    },
    om_mutation_def: {
      head: ['type_name', 'mutation_name', 'description'],
      schema: 'type_name, mutation_name => description',
    },
    om_interceptor_def: {
      head: ['type_name', 'action_name', 'phase', 'seq', 'description'],
      schema: 'type_name, action_name, phase, seq => description',
    },
    om_behavior_binding: {
      head: [
        'behavior_kind',
        'owner_type',
        'behavior_name',
        'callback_slot',
        'phase',
        'seq',
        'binding_id',
      ],
      schema:
        'behavior_kind, owner_type, behavior_name, callback_slot, phase, seq => binding_id',
    },
  };

  for (const relation of BEHAVIOR_RELATIONS) {
    const { head, schema } = schemasByRelation[relation];
    await db.run(
      `
?[${head.join(', ')}] <- $rows
:replace ${relation} { ${schema} }
      `.trim(),
      { rows: behavior[relation] || [] }
    );
  }
}

async function defineMigrationBehaviorFixture(runtime, om) {
  await om.defineType(runtime, 'MigrationBehaviorOwner', 'Migration behavior owner');
  await om.defineConstraint(runtime, 'MigrationBehaviorOwner', 'requires_code', {
    scope: 'custom',
    message: 'code required',
    validator: () => null,
  });
  await om.defineComputed(
    runtime,
    'MigrationBehaviorOwner',
    'score',
    () => 1,
    'score computed'
  );
  await om.defineAction(
    runtime,
    'MigrationBehaviorOwner',
    'activate',
    () => [],
    'activate action'
  );
  await om.defineMutation(
    runtime,
    'MigrationBehaviorOwner',
    'touch',
    () => {},
    'touch mutation'
  );
  await om.addInterceptor(
    runtime,
    'MigrationBehaviorOwner',
    'activate',
    'before',
    () => {},
    'before activate'
  );
}

async function defineRollbackBehaviorFixture(db, runtime, om, owner = 'RollbackBehaviorOwner') {
  await om.defineType(runtime, owner, `${owner} type`);
  await om.defineConstraint(runtime, owner, 'guard', {
    scope: 'custom',
    message: 'v1 guard',
    validator: () => null,
  });
  await om.defineComputed(runtime, owner, 'score', () => 1, 'v1 score');
  await om.defineAction(runtime, owner, 'approve', () => [], 'v1 approve');
  await om.defineMutation(runtime, owner, 'touch', () => {}, 'v1 touch');
  await om.addInterceptor(runtime, owner, 'approve', 'after', () => {}, 'v1 after approve');

  await putBinding(db, {
    kind: 'constraint',
    owner,
    name: 'guard',
    slot: 'validator',
    bindingId: 'v1:guard:validator',
  });
  await putBinding(db, {
    kind: 'computed',
    owner,
    name: 'score',
    slot: 'compute',
    bindingId: 'v1:score:compute',
  });
  await putBinding(db, {
    kind: 'action',
    owner,
    name: 'approve',
    slot: 'handler',
    bindingId: 'v1:approve:handler',
  });
  await putBinding(db, {
    kind: 'mutation',
    owner,
    name: 'touch',
    slot: 'executor',
    bindingId: 'v1:touch:executor',
  });
  await putBinding(db, {
    kind: 'interceptor',
    owner,
    name: 'approve',
    slot: 'handler',
    phase: 'after',
    seq: 0,
    bindingId: 'v1:approve:after',
  });
}

function rollbackCurrentBehavior(owner = 'RollbackBehaviorOwner', prefix = 'v2') {
  return behaviorSnapshot({
    om_constraint_def: [[owner, 'guard', 'custom', `${prefix} guard`]],
    om_computed_def: [[owner, 'score', `${prefix} score`]],
    om_action_def: [[owner, 'approve', `${prefix} approve`]],
    om_mutation_def: [[owner, 'touch', `${prefix} touch`]],
    om_interceptor_def: [[owner, 'approve', 'after', 0, `${prefix} after approve`]],
    om_behavior_binding: [
      ['action', owner, 'approve', 'handler', '', -1, `${prefix}:approve:handler`],
      ['computed', owner, 'score', 'compute', '', -1, `${prefix}:score:compute`],
      ['constraint', owner, 'guard', 'validator', '', -1, `${prefix}:guard:validator`],
      ['interceptor', owner, 'approve', 'handler', 'after', 0, `${prefix}:approve:after`],
      ['mutation', owner, 'touch', 'executor', '', -1, `${prefix}:touch:executor`],
    ],
  });
}

function behaviorRowsTotal(behavior) {
  return BEHAVIOR_RELATIONS.reduce(
    (total, relation) => total + behavior[relation].length,
    0
  );
}

function catalogEntry(catalog, kind, ownerType, name, phase = null, seq = null) {
  return catalog.behaviors.find((candidate) =>
    candidate.kind === kind &&
    candidate.ownerType === ownerType &&
    candidate.name === name &&
    candidate.interceptorPhase === phase &&
    candidate.interceptorSeq === seq
  );
}

function expectActionReady(catalog, ownerType, actionName, bindingId) {
  expect(catalogEntry(catalog, 'action', ownerType, actionName).callbacks).toEqual([
    { slot: 'handler', bindingId, readiness: 'ready' },
  ]);
}

async function applyRollbackVersionBump(db, om, migrationId, typeName) {
  await om.applySchemaMigration(db, {
    migrationId,
    fromVersion: 1,
    toVersion: 2,
    label: `${migrationId} v2`,
    steps: [
      {
        kind: 'addType',
        typeName,
        description: `${typeName} current only`,
      },
    ],
  });
}

async function prepareRollbackFailureProbe(db, om, runtime, owner, suffix) {
  await defineRollbackBehaviorFixture(db, runtime, om, owner);
  await om.defineType(db, `RollbackFailureCacheV1${suffix}`, `cache v1 ${suffix}`);
  const target = await om.writeSchemaSnapshot(db, 1, {
    createdAt: `2026-07-18T04:3${suffix.length % 10}:00.000Z`,
    label: `rollback failure target ${suffix}`,
  });

  const currentOnlyType = `RollbackFailureCurrentOnly${suffix}`;
  await applyRollbackVersionBump(
    db,
    om,
    `behavior-rollback-failure-${suffix}`,
    currentOnlyType
  );
  await om.defineType(db, `RollbackFailureCacheV2${suffix}`, `cache v2 ${suffix}`);
  const alias = `RollbackFailureCachedAlias${suffix}`;
  await om.defineTypeAlias(db, alias, `RollbackFailureCacheV2${suffix}`);

  const currentBehavior = rollbackCurrentBehavior(owner, `${suffix}-current`);
  await replaceBehaviorSnapshotRows(db, currentBehavior);
  const currentActionBindingId = `${suffix}-current:approve:handler`;
  om.registerAction(
    runtime,
    owner,
    'approve',
    currentActionBindingId,
    () => []
  );

  return {
    alias,
    currentActionBindingId,
    currentBehavior,
    currentOnlyType,
    expectedAliasTarget: `RollbackFailureCacheV2${suffix}`,
    owner,
    target,
  };
}

async function captureRollbackFailurePrestate(db, om, runtime, wrapped, probe) {
  expect(await typeExists(db, probe.currentOnlyType)).toBe(true);
  expect(await om.resolveType(wrapped, probe.alias)).toBe(probe.expectedAliasTarget);
  expectActionReady(
    await om.getBehaviorCatalog(runtime),
    probe.owner,
    'approve',
    probe.currentActionBindingId
  );

  return {
    aliasTarget: await om.resolveType(wrapped, probe.alias),
    behavior: await readBehaviorSnapshotRows(db),
    catalog: cloneJson(await om.getBehaviorCatalog(runtime)),
    state: await om.getSchemaState(db),
  };
}

async function expectRollbackFailurePreserved(db, om, runtime, wrapped, probe, before) {
  expect(await typeExists(db, probe.currentOnlyType)).toBe(true);
  expect(await readBehaviorSnapshotRows(db)).toEqual(before.behavior);
  expect(await om.getSchemaState(db)).toEqual(before.state);
  expect(cloneJson(await om.getBehaviorCatalog(runtime))).toEqual(before.catalog);
  expect(await om.resolveType(wrapped, probe.alias)).toBe(before.aliasTarget);
}

function replacedRelationName(script) {
  const match = String(script || '').match(/:replace\s+([A-Za-z0-9_]+)/);
  return match ? match[1] : null;
}

function snapshotWithResolvedBehavior(snapshot, behavior) {
  const effective = cloneJson(snapshot);
  effective.behavior = behavior;
  return effective;
}

function createRollbackTransactionBlocker() {
  let releaseRollback;
  let enteredRollback;
  const entered = new Promise((resolve) => {
    enteredRollback = resolve;
  });
  const release = new Promise((resolve) => {
    releaseRollback = resolve;
  });
  let armed = false;
  let delayed = false;
  let released = false;

  return {
    hooks: {
      async beforeRun(_transactionNumber, script) {
        if (
          armed &&
          !delayed &&
          String(script || '').includes(':replace om_type')
        ) {
          delayed = true;
          enteredRollback();
          await release;
        }
      },
    },
    arm() {
      armed = true;
    },
    entered,
    release() {
      if (released) return;
      released = true;
      releaseRollback();
    },
    get delayed() {
      return delayed;
    },
  };
}

async function expectPromisePending(promise) {
  let settled = false;
  promise.then(
    () => {
      settled = true;
    },
    () => {
      settled = true;
    }
  );
  await Bun.sleep(20);
  expect(settled).toBe(false);
}

async function settleQuietly(promise) {
  if (!promise || typeof promise.then !== 'function') return;
  try {
    await Promise.race([
      promise,
      Bun.sleep(100),
    ]);
  } catch (_) {
  }
}

async function promiseOutcome(promise, timeoutMs = 200) {
  return Promise.race([
    promise.then(
      (value) => ({ status: 'fulfilled', value }),
      (reason) => ({ status: 'rejected', reason })
    ),
    Bun.sleep(timeoutMs).then(() => ({ status: 'timeout' })),
  ]);
}

function actionManifest(om, owner, action, bindingId, description = 'imported action') {
  return om.encodeBehaviorManifestJson({
    behaviors: [{
      kind: 'action',
      ownerType: owner,
      name: action,
      constraintType: null,
      message: null,
      description,
      interceptorPhase: null,
      interceptorSeq: null,
      callbacks: [{
        slot: 'handler',
        bindingId,
        readiness: 'ready',
      }],
    }],
  });
}

function actionOnlyBehavior(owner, prefix) {
  return behaviorSnapshot({
    om_action_def: [[owner, 'approve', `${prefix} approve`]],
    om_behavior_binding: [
      ['action', owner, 'approve', 'handler', '', -1, `${prefix}:approve:handler`],
    ],
  });
}

async function prepareRollbackGateProbe(om, db, owner, migrationId, currentPrefix) {
  await om.defineType(db, owner, `${owner} type`);
  await putActionDefinition(db, owner, 'approve', 'v1 approve');
  await putBinding(db, {
    kind: 'action',
    owner,
    name: 'approve',
    slot: 'handler',
    bindingId: 'v1:approve:handler',
  });
  await om.createEntity(db, `${owner}:1`, owner, `${owner} entity`);
  const target = await om.writeSchemaSnapshot(db, 1, {
    createdAt: '2026-07-18T05:00:00.000Z',
    label: `${owner} rollback gate target`,
  });
  await applyRollbackVersionBump(
    db,
    om,
    migrationId,
    `${owner}CurrentOnlyType`
  );
  await replaceBehaviorSnapshotRows(db, actionOnlyBehavior(owner, currentPrefix));
  return target;
}

async function prepareRollbackConstraintProbe(om, runtime, owner) {
  await om.defineType(runtime, owner, `${owner} type`);
  await om.createEntity(runtime, `${owner}:1`, owner, `${owner} entity`);
}

async function materializeRollbackConstraintTarget(om, db, owner, migrationId) {
  await om.writeSchemaSnapshot(db, 1, {
    createdAt: '2026-07-18T05:30:00.000Z',
    label: `${owner} rollback constraint target`,
  });
  await applyRollbackVersionBump(
    db,
    om,
    migrationId,
    `${owner}CurrentOnlyType`
  );
}

async function diffStoredBehaviorSnapshots(db, om, fromBehavior, toBehavior, options = {}) {
  const base = await om.writeSchemaSnapshot(db, 1, {
    createdAt: '2026-07-18T00:00:00.000Z',
  });
  const fromSnapshot = cloneJson(base);
  const toSnapshot = cloneJson(base);

  fromSnapshot.version = 1;
  toSnapshot.version = 2;
  toSnapshot.schema.om_type = [
    ...toSnapshot.schema.om_type,
    ['BehaviorDiffNonBehaviorType', 'proves the existing schema diff still runs', null],
  ];

  if (fromBehavior === undefined) {
    delete fromSnapshot.behavior;
  } else {
    fromSnapshot.behavior = fromBehavior;
  }
  if (toBehavior === undefined) {
    delete toSnapshot.behavior;
  } else {
    toSnapshot.behavior = toBehavior;
  }

  if (typeof options.mutateFrom === 'function') options.mutateFrom(fromSnapshot);
  if (typeof options.mutateTo === 'function') options.mutateTo(toSnapshot);

  await putStoredSnapshot(db, fromSnapshot);
  await putStoredSnapshot(db, toSnapshot);
  return om.diffSchemaVersions(db, 1, 2);
}

function expectExistingSchemaDiffStillWorks(diff) {
  expect(diff.schema.om_type.added).toContainEqual({
    name: 'BehaviorDiffNonBehaviorType',
    description: 'proves the existing schema diff still runs',
    parent_type: null,
  });
}

function requireBehaviorDiff(diff) {
  expect(diff).toHaveProperty('behavior');
  return diff.behavior;
}

describe('OM behavior schema versioning snapshot contract', () => {
  test('writes an explicit empty Behavior Snapshot V1 section', async () => {
    const { db, om } = await createTestDb();

    try {
      const snapshot = await om.writeSchemaSnapshot(db, 1, {
        createdAt: '2026-07-18T00:00:00.000Z',
      });

      expect(snapshot).toHaveProperty('behavior');
      expect(snapshot.behavior).toEqual({
        formatVersion: 1,
        om_constraint_def: [],
        om_computed_def: [],
        om_action_def: [],
        om_mutation_def: [],
        om_interceptor_def: [],
        om_behavior_binding: [],
      });
      expect(Object.keys(snapshot.behavior)).toEqual([
        'formatVersion',
        ...BEHAVIOR_RELATIONS,
      ]);
    } finally {
      db.close();
      await om.clearRegistry();
    }
  });

  test('captures deterministic definitions and binding identities without runtime payloads', async () => {
    const { db, om } = await createTestDb();
    const runtime = om.createOmRuntime(db);
    const unicodeOwner = 'Owner\u4E2D\u6587\u{1F600}';

    try {
      await om.defineType(runtime, 'ZuluOwner', 'Zulu owner');
      await om.defineType(runtime, unicodeOwner, 'Owner metadata \u03A9 \u{1F600}');

      await om.defineConstraint(runtime, 'ZuluOwner', 'custom_rule', {
        scope: 'custom',
        message: 'custom \u03A9',
        validator: () => null,
      });
      await om.defineConstraint(runtime, unicodeOwner, 'conditional_rule', {
        scope: 'conditional',
        message: '\u9700\u68C0\u67E5 "\u6765\u6E90" \u{1F600}',
        when: () => true,
        then: () => true,
      });

      await om.defineComputed(
        runtime,
        'ZuluOwner',
        'rank',
        () => 2,
        'Zulu rank'
      );
      await om.defineComputed(
        runtime,
        unicodeOwner,
        'score',
        () => 1,
        '\u8BA1\u7B97\t\u503C \u{1F600}'
      );

      await om.defineAction(
        runtime,
        'ZuluOwner',
        'archive',
        () => [],
        'Zulu archive'
      );
      await om.defineAction(
        runtime,
        unicodeOwner,
        'deploy',
        () => [],
        '\u90E8\u7F72 \u{1F600}'
      );

      await om.defineMutation(
        runtime,
        'ZuluOwner',
        'purge',
        () => {},
        'Zulu purge'
      );
      await om.defineMutation(
        runtime,
        unicodeOwner,
        'persist',
        () => {},
        '\u4FDD\u5B58 \u{1F600}'
      );

      await om.addInterceptor(
        runtime,
        'ZuluOwner',
        'archive',
        'before',
        () => {},
        'Zulu before'
      );
      await om.addInterceptor(
        runtime,
        unicodeOwner,
        'deploy',
        'after',
        () => {},
        '\u540E\u7F6E \u{1F600}'
      );

      for (const seq of [10, 2]) {
        await putInterceptor(db, {
          owner: unicodeOwner,
          action: 'deploy',
          phase: 'after',
          seq,
          description: `numeric order ${seq}`,
        });
      }

      const bindings = [
        ['mutation', unicodeOwner, 'persist', 'executor', '', -1, 'binding:executor'],
        ['interceptor', unicodeOwner, 'deploy', 'handler', 'after', 0, 'binding:interceptor'],
        ['interceptor', unicodeOwner, 'deploy', 'handler', 'after', 10, 'binding:interceptor:10'],
        ['interceptor', unicodeOwner, 'deploy', 'handler', 'after', 2, 'binding:interceptor:2'],
        ['constraint', 'ZuluOwner', 'custom_rule', 'validator', '', -1, 'binding:validator'],
        ['constraint', unicodeOwner, 'conditional_rule', 'when', '', -1, 'binding:when'],
        ['action', unicodeOwner, 'deploy', 'handler', '', -1, 'binding:action'],
        ['constraint', unicodeOwner, 'conditional_rule', 'then', '', -1, 'binding:then'],
        ['computed', unicodeOwner, 'score', 'compute', '', -1, 'binding:compute'],
      ];
      for (const [kind, owner, name, slot, phase, seq, bindingId] of bindings) {
        await putBinding(db, { kind, owner, name, slot, phase, seq, bindingId });
      }

      function callbackFunctionSourceMarker() {
        return 'callback-function-source-marker';
      }
      callbackFunctionSourceMarker.scriptSource = 'script-source-marker';
      om.registerAction(
        runtime,
        unicodeOwner,
        'runtime_only',
        'runtime-only-registration-marker',
        callbackFunctionSourceMarker
      );

      const snapshot = await om.writeSchemaSnapshot(runtime, 1, {
        createdAt: '2026-07-18T00:00:00.000Z',
        label: '\u884C\u4E3A\u7248\u672C \u{1F600}',
      });

      expect(snapshot).toHaveProperty('behavior');
      expect(Object.keys(snapshot.behavior)).toEqual([
        'formatVersion',
        ...BEHAVIOR_RELATIONS,
      ]);
      expect(snapshot.behavior).toEqual({
        formatVersion: 1,
        om_constraint_def: [
          [
            unicodeOwner,
            'conditional_rule',
            'conditional',
            '\u9700\u68C0\u67E5 "\u6765\u6E90" \u{1F600}',
          ],
          ['ZuluOwner', 'custom_rule', 'custom', 'custom \u03A9'],
        ],
        om_computed_def: [
          [unicodeOwner, 'score', '\u8BA1\u7B97\t\u503C \u{1F600}'],
          ['ZuluOwner', 'rank', 'Zulu rank'],
        ],
        om_action_def: [
          [unicodeOwner, 'deploy', '\u90E8\u7F72 \u{1F600}'],
          ['ZuluOwner', 'archive', 'Zulu archive'],
        ],
        om_mutation_def: [
          [unicodeOwner, 'persist', '\u4FDD\u5B58 \u{1F600}'],
          ['ZuluOwner', 'purge', 'Zulu purge'],
        ],
        om_interceptor_def: [
          [unicodeOwner, 'deploy', 'after', 0, '\u540E\u7F6E \u{1F600}'],
          [unicodeOwner, 'deploy', 'after', 2, 'numeric order 2'],
          [unicodeOwner, 'deploy', 'after', 10, 'numeric order 10'],
          ['ZuluOwner', 'archive', 'before', 0, 'Zulu before'],
        ],
        om_behavior_binding: [
          ['action', unicodeOwner, 'deploy', 'handler', '', -1, 'binding:action'],
          ['computed', unicodeOwner, 'score', 'compute', '', -1, 'binding:compute'],
          ['constraint', unicodeOwner, 'conditional_rule', 'then', '', -1, 'binding:then'],
          ['constraint', unicodeOwner, 'conditional_rule', 'when', '', -1, 'binding:when'],
          ['constraint', 'ZuluOwner', 'custom_rule', 'validator', '', -1, 'binding:validator'],
          [
            'interceptor',
            unicodeOwner,
            'deploy',
            'handler',
            'after',
            0,
            'binding:interceptor',
          ],
          [
            'interceptor',
            unicodeOwner,
            'deploy',
            'handler',
            'after',
            2,
            'binding:interceptor:2',
          ],
          [
            'interceptor',
            unicodeOwner,
            'deploy',
            'handler',
            'after',
            10,
            'binding:interceptor:10',
          ],
          ['mutation', unicodeOwner, 'persist', 'executor', '', -1, 'binding:executor'],
        ],
      });

      const interceptorSequences = snapshot.behavior.om_interceptor_def
        .filter(
          ([owner, action, phase, seq]) =>
            owner === unicodeOwner &&
            action === 'deploy' &&
            phase === 'after' &&
            seq > 0
        )
        .map((row) => row[3]);
      expect(interceptorSequences).toEqual([2, 10]);

      const interceptorBindingSequences =
        snapshot.behavior.om_behavior_binding
          .filter(
            ([kind, owner, action, slot, phase, seq]) =>
              kind === 'interceptor' &&
              owner === unicodeOwner &&
              action === 'deploy' &&
              slot === 'handler' &&
              phase === 'after' &&
              seq > 0
          )
          .map((row) => row[5]);
      expect(interceptorBindingSequences).toEqual([2, 10]);

      const callbackSlots = new Set(
        snapshot.behavior.om_behavior_binding.map((row) => row[3])
      );
      expect(callbackSlots).toEqual(
        new Set(['validator', 'when', 'then', 'compute', 'handler', 'executor'])
      );

      const serialized = JSON.stringify(snapshot);
      expect(serialized).not.toContain('callback-function-source-marker');
      expect(serialized).not.toContain('script-source-marker');
      expect(serialized).not.toContain('runtime-only-registration-marker');
      expect(serialized).not.toContain('readiness');
      for (const value of Object.values(snapshot.behavior)) {
        expect(typeof value).not.toBe('function');
      }
    } finally {
      db.close();
      await om.clearRegistry(runtime);
    }
  });
});

describe('OM behavior schema versioning deterministic diff contract', () => {
  test('diffs add, remove, and metadata or binding identity updates across all six relations', async () => {
    const { db, om } = await createTestDb();

    try {
      const fromBehavior = behaviorSnapshot({
        om_constraint_def: [
          ['Owner', 'removed_constraint', 'custom', 'removed constraint'],
          ['Owner', 'updated_constraint', 'custom', 'old constraint message'],
        ],
        om_computed_def: [
          ['Owner', 'removed_computed', 'removed computed'],
          ['Owner', 'updated_computed', 'old computed description'],
        ],
        om_action_def: [
          ['Owner', 'removed_action', 'removed action'],
          ['Owner', 'updated_action', 'old action description'],
        ],
        om_mutation_def: [
          ['Owner', 'removed_mutation', 'removed mutation'],
          ['Owner', 'updated_mutation', 'old mutation description'],
        ],
        om_interceptor_def: [
          ['Owner', 'run', 'after', 10, 'removed interceptor'],
          ['Owner', 'run', 'before', 1, 'old interceptor description'],
        ],
        om_behavior_binding: [
          ['action', 'Owner', 'removed_action', 'handler', '', -1, 'binding:removed'],
          ['action', 'Owner', 'updated_action', 'handler', '', -1, 'binding:old'],
        ],
      });
      const toBehavior = behaviorSnapshot({
        om_constraint_def: [
          ['Owner', 'updated_constraint', 'conditional', 'new constraint message'],
          ['Owner', 'added_constraint', 'custom', 'added constraint'],
        ],
        om_computed_def: [
          ['Owner', 'updated_computed', 'new computed description'],
          ['Owner', 'added_computed', 'added computed'],
        ],
        om_action_def: [
          ['Owner', 'updated_action', 'new action description'],
          ['Owner', 'added_action', 'added action'],
        ],
        om_mutation_def: [
          ['Owner', 'updated_mutation', 'new mutation description'],
          ['Owner', 'added_mutation', 'added mutation'],
        ],
        om_interceptor_def: [
          ['Owner', 'run', 'before', 1, 'new interceptor description'],
          ['Owner', 'run', 'after', 2, 'added interceptor'],
        ],
        om_behavior_binding: [
          ['action', 'Owner', 'updated_action', 'handler', '', -1, 'binding:new'],
          ['computed', 'Owner', 'added_computed', 'compute', '', -1, 'binding:added'],
        ],
      });

      const diff = await diffStoredBehaviorSnapshots(
        db,
        om,
        fromBehavior,
        toBehavior
      );
      expectExistingSchemaDiffStillWorks(diff);
      const behavior = requireBehaviorDiff(diff);

      expect(behavior).toEqual({
        fromPresence: 'present',
        toPresence: 'present',
        comparable: true,
        diagnostics: [],
        relations: {
          om_constraint_def: {
            added: [
              {
                type_name: 'Owner',
                constraint_name: 'added_constraint',
                constraint_type: 'custom',
                message: 'added constraint',
              },
            ],
            removed: [
              {
                type_name: 'Owner',
                constraint_name: 'removed_constraint',
                constraint_type: 'custom',
                message: 'removed constraint',
              },
            ],
            updated: [
              {
                key: {
                  type_name: 'Owner',
                  constraint_name: 'updated_constraint',
                },
                from: {
                  type_name: 'Owner',
                  constraint_name: 'updated_constraint',
                  constraint_type: 'custom',
                  message: 'old constraint message',
                },
                to: {
                  type_name: 'Owner',
                  constraint_name: 'updated_constraint',
                  constraint_type: 'conditional',
                  message: 'new constraint message',
                },
              },
            ],
          },
          om_computed_def: {
            added: [
              {
                type_name: 'Owner',
                attr_name: 'added_computed',
                description: 'added computed',
              },
            ],
            removed: [
              {
                type_name: 'Owner',
                attr_name: 'removed_computed',
                description: 'removed computed',
              },
            ],
            updated: [
              {
                key: { type_name: 'Owner', attr_name: 'updated_computed' },
                from: {
                  type_name: 'Owner',
                  attr_name: 'updated_computed',
                  description: 'old computed description',
                },
                to: {
                  type_name: 'Owner',
                  attr_name: 'updated_computed',
                  description: 'new computed description',
                },
              },
            ],
          },
          om_action_def: {
            added: [
              {
                type_name: 'Owner',
                action_name: 'added_action',
                description: 'added action',
              },
            ],
            removed: [
              {
                type_name: 'Owner',
                action_name: 'removed_action',
                description: 'removed action',
              },
            ],
            updated: [
              {
                key: { type_name: 'Owner', action_name: 'updated_action' },
                from: {
                  type_name: 'Owner',
                  action_name: 'updated_action',
                  description: 'old action description',
                },
                to: {
                  type_name: 'Owner',
                  action_name: 'updated_action',
                  description: 'new action description',
                },
              },
            ],
          },
          om_mutation_def: {
            added: [
              {
                type_name: 'Owner',
                mutation_name: 'added_mutation',
                description: 'added mutation',
              },
            ],
            removed: [
              {
                type_name: 'Owner',
                mutation_name: 'removed_mutation',
                description: 'removed mutation',
              },
            ],
            updated: [
              {
                key: {
                  type_name: 'Owner',
                  mutation_name: 'updated_mutation',
                },
                from: {
                  type_name: 'Owner',
                  mutation_name: 'updated_mutation',
                  description: 'old mutation description',
                },
                to: {
                  type_name: 'Owner',
                  mutation_name: 'updated_mutation',
                  description: 'new mutation description',
                },
              },
            ],
          },
          om_interceptor_def: {
            added: [
              {
                type_name: 'Owner',
                action_name: 'run',
                phase: 'after',
                seq: 2,
                description: 'added interceptor',
              },
            ],
            removed: [
              {
                type_name: 'Owner',
                action_name: 'run',
                phase: 'after',
                seq: 10,
                description: 'removed interceptor',
              },
            ],
            updated: [
              {
                key: {
                  type_name: 'Owner',
                  action_name: 'run',
                  phase: 'before',
                  seq: 1,
                },
                from: {
                  type_name: 'Owner',
                  action_name: 'run',
                  phase: 'before',
                  seq: 1,
                  description: 'old interceptor description',
                },
                to: {
                  type_name: 'Owner',
                  action_name: 'run',
                  phase: 'before',
                  seq: 1,
                  description: 'new interceptor description',
                },
              },
            ],
          },
          om_behavior_binding: {
            added: [
              {
                behavior_kind: 'computed',
                owner_type: 'Owner',
                behavior_name: 'added_computed',
                callback_slot: 'compute',
                phase: '',
                seq: -1,
                binding_id: 'binding:added',
              },
            ],
            removed: [
              {
                behavior_kind: 'action',
                owner_type: 'Owner',
                behavior_name: 'removed_action',
                callback_slot: 'handler',
                phase: '',
                seq: -1,
                binding_id: 'binding:removed',
              },
            ],
            updated: [
              {
                key: {
                  behavior_kind: 'action',
                  owner_type: 'Owner',
                  behavior_name: 'updated_action',
                  callback_slot: 'handler',
                  phase: '',
                  seq: -1,
                },
                from: {
                  behavior_kind: 'action',
                  owner_type: 'Owner',
                  behavior_name: 'updated_action',
                  callback_slot: 'handler',
                  phase: '',
                  seq: -1,
                  binding_id: 'binding:old',
                },
                to: {
                  behavior_kind: 'action',
                  owner_type: 'Owner',
                  behavior_name: 'updated_action',
                  callback_slot: 'handler',
                  phase: '',
                  seq: -1,
                  binding_id: 'binding:new',
                },
              },
            ],
          },
        },
      });
      expect(Object.keys(behavior.relations)).toEqual(BEHAVIOR_RELATIONS);
    } finally {
      db.close();
      await om.clearRegistry();
    }
  });

  test('sorts shuffled relation changes by ordinal tuple keys and numeric seq', async () => {
    const { db, om } = await createTestDb();

    try {
      const diff = await diffStoredBehaviorSnapshots(
        db,
        om,
        behaviorSnapshot(),
        behaviorSnapshot({
          om_constraint_def: [
            ['ZuluOwner', 'rule', 'custom', 'zulu'],
            ['AlphaOwner', 'z_rule', 'custom', 'alpha z'],
            ['AlphaOwner', 'a_rule', 'custom', 'alpha a'],
          ],
          om_interceptor_def: [
            ['Owner', 'run', 'after', 10, 'ten'],
            ['Owner', 'run', 'after', 2, 'two'],
          ],
          om_behavior_binding: [
            ['interceptor', 'Owner', 'run', 'handler', 'after', 10, 'binding:10'],
            ['interceptor', 'Owner', 'run', 'handler', 'after', 2, 'binding:2'],
          ],
        })
      );
      expectExistingSchemaDiffStillWorks(diff);
      const behavior = requireBehaviorDiff(diff);

      expect(Object.keys(behavior.relations)).toEqual(BEHAVIOR_RELATIONS);
      expect(
        behavior.relations.om_constraint_def.added.map(
          ({ type_name, constraint_name }) => [type_name, constraint_name]
        )
      ).toEqual([
        ['AlphaOwner', 'a_rule'],
        ['AlphaOwner', 'z_rule'],
        ['ZuluOwner', 'rule'],
      ]);
      expect(
        behavior.relations.om_interceptor_def.added.map(({ seq }) => seq)
      ).toEqual([2, 10]);
      expect(
        behavior.relations.om_behavior_binding.added.map(({ seq }) => seq)
      ).toEqual([2, 10]);
    } finally {
      db.close();
      await om.clearRegistry();
    }
  });

  test('reports duplicate keys and invalid row shapes with stable structured diagnostics', async () => {
    const { db, om } = await createTestDb();

    try {
      const diff = await diffStoredBehaviorSnapshots(
        db,
        om,
        behaviorSnapshot({
          om_action_def: [
            ['Owner', 'duplicate', 'first'],
            ['Owner', 'duplicate', 'second'],
          ],
        }),
        behaviorSnapshot({
          om_computed_def: [['Owner', 'missing_description']],
        })
      );
      expectExistingSchemaDiffStillWorks(diff);
      const behavior = requireBehaviorDiff(diff);

      expect(behavior).toEqual({
        fromPresence: 'present',
        toPresence: 'present',
        comparable: false,
        diagnostics: [
          {
            code: 'OMSV1002',
            path: '$.behavior.om_action_def[1]',
            message:
              'Duplicate key in from behavior snapshot relation om_action_def.',
            side: 'from',
            relation: 'om_action_def',
            key: {
              type_name: 'Owner',
              action_name: 'duplicate',
            },
          },
          {
            code: 'OMSV1003',
            path: '$.behavior.om_computed_def[0]',
            message:
              'Invalid row shape in to behavior snapshot relation om_computed_def: expected 3 columns, received 2.',
            side: 'to',
            relation: 'om_computed_def',
            expectedColumns: 3,
            actualColumns: 2,
          },
        ],
        relations: {},
      });
    } finally {
      db.close();
      await om.clearRegistry();
    }
  });

  test('keeps behavior unknown when either stored snapshot is legacy missing', async () => {
    const { db, om } = await createTestDb();

    try {
      const missingFrom = await diffStoredBehaviorSnapshots(
        db,
        om,
        undefined,
        behaviorSnapshot()
      );
      expectExistingSchemaDiffStillWorks(missingFrom);
      expect(requireBehaviorDiff(missingFrom)).toEqual({
        fromPresence: 'missing',
        toPresence: 'present',
        comparable: false,
        diagnostics: [
          {
            code: 'OMSV1001',
            path: '$.behavior',
            message:
              'Cannot compare behavior schema: from snapshot is missing the behavior section.',
          },
        ],
        relations: {},
      });

      const missingTo = await diffStoredBehaviorSnapshots(
        db,
        om,
        behaviorSnapshot(),
        undefined
      );
      expectExistingSchemaDiffStillWorks(missingTo);
      expect(requireBehaviorDiff(missingTo)).toEqual({
        fromPresence: 'present',
        toPresence: 'missing',
        comparable: false,
        diagnostics: [
          {
            code: 'OMSV1001',
            path: '$.behavior',
            message:
              'Cannot compare behavior schema: to snapshot is missing the behavior section.',
          },
        ],
        relations: {},
      });
    } finally {
      db.close();
      await om.clearRegistry();
    }
  });
});

describe('OM behavior schema versioning migration snapshot contract', () => {
  test('materializes missing source and target snapshots with non-empty behavior at their transaction points', async () => {
    const { db, om } = await createTestDb();
    const runtime = om.createOmRuntime(db);

    try {
      expect(await om.readSchemaSnapshot(db, 1)).toBe(null);

      await defineMigrationBehaviorFixture(runtime, om);
      await putBinding(db, {
        kind: 'constraint',
        owner: 'MigrationBehaviorOwner',
        name: 'requires_code',
        slot: 'validator',
        bindingId: 'migration:constraint:validator',
      });
      await putBinding(db, {
        kind: 'action',
        owner: 'MigrationBehaviorOwner',
        name: 'activate',
        slot: 'handler',
        bindingId: 'migration:action:handler',
      });

      await om.applySchemaMigration(db, {
        migrationId: 'behavior-migration-nonempty',
        fromVersion: 1,
        toVersion: 2,
        label: 'v2 behavior-aware migration',
        steps: [
          {
            kind: 'addType',
            typeName: 'MigrationTargetType',
            description: 'created after source snapshot',
          },
        ],
      });

      const source = await om.readSchemaSnapshot(db, 1);
      const target = await om.readSchemaSnapshot(db, 2);

      expect(source.behavior).toHaveProperty('formatVersion', 1);
      expect(target.behavior).toHaveProperty('formatVersion', 1);
      expectBehaviorSectionKeys(source.behavior);
      expectBehaviorSectionKeys(target.behavior);
      expect(behaviorRowsTotal(source.behavior)).toBeGreaterThan(0);
      expect(target.behavior).toEqual(source.behavior);

      expect(source.schema.om_type.map((row) => row[0])).not.toContain(
        'MigrationTargetType'
      );
      expect(target.schema.om_type.map((row) => row[0])).toContain(
        'MigrationTargetType'
      );

      const targetChecksum =
        await expectTargetChecksumMatchesStoredStateAndVersion(db, om, target);
      const withoutBehavior = cloneJson(target);
      delete withoutBehavior.behavior;
      expect(targetChecksum).not.toBe(schemaChecksum(withoutBehavior));
    } finally {
      await om.clearRegistry(runtime);
      db.close();
    }
  });

  test('materializes explicit empty behavior sections for both migration snapshots', async () => {
    const { db, om } = await createTestDb();

    try {
      await om.applySchemaMigration(db, {
        migrationId: 'behavior-migration-empty',
        fromVersion: 1,
        toVersion: 2,
        label: 'v2 empty behavior-aware migration',
        steps: [
          {
            kind: 'addType',
            typeName: 'EmptyBehaviorMigrationTarget',
            description: 'target only type',
          },
        ],
      });

      const source = await om.readSchemaSnapshot(db, 1);
      const target = await om.readSchemaSnapshot(db, 2);

      expect(source.behavior).toEqual(behaviorSnapshot());
      expect(target.behavior).toEqual(behaviorSnapshot());
      expect(source.schema.om_type.map((row) => row[0])).not.toContain(
        'EmptyBehaviorMigrationTarget'
      );
      expect(target.schema.om_type.map((row) => row[0])).toContain(
        'EmptyBehaviorMigrationTarget'
      );
      const targetChecksum =
        await expectTargetChecksumMatchesStoredStateAndVersion(db, om, target);
      const withoutBehavior = cloneJson(target);
      delete withoutBehavior.behavior;
      expect(targetChecksum).not.toBe(schemaChecksum(withoutBehavior));
    } finally {
      db.close();
      await om.clearRegistry();
    }
  });

  test('does not backfill an existing legacy source snapshot while hashing the behavior-aware target exactly', async () => {
    const { db, om } = await createTestDb();
    const runtime = om.createOmRuntime(db);

    try {
      await defineMigrationBehaviorFixture(runtime, om);
      await putBinding(db, {
        kind: 'constraint',
        owner: 'MigrationBehaviorOwner',
        name: 'requires_code',
        slot: 'validator',
        bindingId: 'legacy-source:constraint:validator',
      });

      const behaviorAwareSource = await om.writeSchemaSnapshot(db, 1, {
        createdAt: '2026-07-18T00:00:00.000Z',
        label: 'legacy source fixture',
      });
      expect(behaviorRowsTotal(behaviorAwareSource.behavior)).toBeGreaterThan(0);

      const legacySource = cloneJson(behaviorAwareSource);
      delete legacySource.behavior;
      await putStoredSnapshot(db, legacySource);
      expect(await om.readSchemaSnapshot(db, 1)).toEqual(legacySource);

      await om.applySchemaMigration(db, {
        migrationId: 'behavior-migration-preserves-legacy-source',
        fromVersion: 1,
        toVersion: 2,
        label: 'v2 keeps source legacy',
        steps: [
          {
            kind: 'addType',
            typeName: 'LegacySourceMigrationTarget',
            description: 'target after existing legacy source',
          },
        ],
      });

      const source = await om.readSchemaSnapshot(db, 1);
      const target = await om.readSchemaSnapshot(db, 2);

      expect(source).toEqual(legacySource);
      expect(source).not.toHaveProperty('behavior');
      expect(target.behavior).toEqual(behaviorAwareSource.behavior);
      expect(target.schema.om_type.map((row) => row[0])).toContain(
        'LegacySourceMigrationTarget'
      );
      const targetChecksum =
        await expectTargetChecksumMatchesStoredStateAndVersion(db, om, target);
      const targetWithoutBehavior = cloneJson(target);
      delete targetWithoutBehavior.behavior;
      expect(targetChecksum).not.toBe(schemaChecksum(targetWithoutBehavior));
    } finally {
      await om.clearRegistry(runtime);
      db.close();
    }
  });

  test('step failure rolls back materialized snapshots, state, log, and behavior facts', async () => {
    const { db, om } = await createTestDb();
    const runtime = om.createOmRuntime(db);

    try {
      await defineMigrationBehaviorFixture(runtime, om);
      await putBinding(db, {
        kind: 'mutation',
        owner: 'MigrationBehaviorOwner',
        name: 'touch',
        slot: 'executor',
        bindingId: 'migration:mutation:executor',
      });

      const beforeBehaviorRows = {};
      for (const relation of BEHAVIOR_RELATIONS) {
        beforeBehaviorRows[relation] = await readBehaviorRelationRows(db, relation);
      }
      const state0 = await om.getSchemaState(db);
      expect(await om.readSchemaSnapshot(db, 1)).toBe(null);

      await expect(
        om.applySchemaMigration(db, {
          migrationId: 'behavior-migration-fails',
          fromVersion: 1,
          toVersion: 2,
          steps: [
            {
              kind: 'addType',
              typeName: 'RolledBackMigrationType',
              description: 'must not survive failure',
            },
            { kind: 'unsupportedBehaviorMigrationStep' },
          ],
        })
      ).rejects.toThrow(/unsupported\s+migration\s+step/i);

      expect(await om.readSchemaSnapshot(db, 1)).toBe(null);
      expect(await om.readSchemaSnapshot(db, 2)).toBe(null);
      expect(await readStoredMigrationStatus(db, 'behavior-migration-fails')).toBe(null);
      expect(await typeExists(db, 'RolledBackMigrationType')).toBe(false);
      expect(await om.getSchemaState(db)).toEqual(state0);

      for (const relation of BEHAVIOR_RELATIONS) {
        expect(await readBehaviorRelationRows(db, relation)).toEqual(
          beforeBehaviorRows[relation]
        );
      }
    } finally {
      await om.clearRegistry(runtime);
      db.close();
    }
  });
});

describe('OM behavior schema versioning rollback contract', () => {
  test('restores a target snapshot with non-empty behavior across all six persistent relations', async () => {
    const { db, om } = await createTestDb();
    const runtime = om.createOmRuntime(db);
    const owner = 'RollbackBehaviorOwner';

    try {
      await defineRollbackBehaviorFixture(db, runtime, om, owner);
      const target = await om.writeSchemaSnapshot(db, 1, {
        createdAt: '2026-07-18T04:00:00.000Z',
        label: 'rollback behavior v1',
      });
      expectBehaviorSectionKeys(target.behavior);
      expect(behaviorRowsTotal(target.behavior)).toBeGreaterThan(0);

      await applyRollbackVersionBump(
        db,
        om,
        'behavior-rollback-present',
        'RollbackCurrentOnlyType'
      );
      const currentBehavior = rollbackCurrentBehavior(owner, 'v2');
      await replaceBehaviorSnapshotRows(db, currentBehavior);
      expect(await readBehaviorSnapshotRows(db)).toEqual(currentBehavior);

      const result = await om.rollbackSchema(db, 1);

      expect(await readBehaviorSnapshotRows(db)).toEqual(target.behavior);
      expect(await typeExists(db, 'RollbackCurrentOnlyType')).toBe(false);
      expect(await om.getSchemaState(db)).toEqual({
        currentVersion: 1,
        checksum: schemaChecksum(target),
      });
      expect(result).toMatchObject({
        ok: true,
        strict: true,
        targetVersion: 1,
        fromVersion: 2,
        diagnostics: [],
        compatibilityDiagnostics: [],
        behaviorPolicyApplied: 'snapshot',
      });
    } finally {
      await om.clearRegistry(runtime);
      db.close();
    }
  });

  test('restores an explicit empty behavior section, ignores legacy policy, and preserves runtime callbacks', async () => {
    const { db, om } = await createTestDb();
    const runtime = om.createOmRuntime(db);
    const owner = 'RollbackEmptyOwner';

    try {
      await om.defineType(runtime, owner, 'owner whose target behavior is empty');
      const target = await om.writeSchemaSnapshot(db, 1, {
        createdAt: '2026-07-18T04:05:00.000Z',
        label: 'rollback empty behavior v1',
      });
      expect(target.behavior).toEqual(behaviorSnapshot());

      await applyRollbackVersionBump(
        db,
        om,
        'behavior-rollback-empty',
        'RollbackEmptyCurrentOnlyType'
      );
      const currentBehavior = rollbackCurrentBehavior(owner, 'empty-current');
      await replaceBehaviorSnapshotRows(db, currentBehavior);
      om.registerAction(
        runtime,
        owner,
        'approve',
        'empty-current:approve:handler',
        () => []
      );
      expectActionReady(
        await om.getBehaviorCatalog(runtime),
        owner,
        'approve',
        'empty-current:approve:handler'
      );

      const result = await om.rollbackSchema(db, 1, {
        legacyBehaviorPolicy: 'clear',
      });

      expect(await readBehaviorSnapshotRows(db)).toEqual(behaviorSnapshot());
      expect(result).toMatchObject({
        ok: true,
        strict: true,
        targetVersion: 1,
        fromVersion: 2,
        diagnostics: [],
        compatibilityDiagnostics: [],
        behaviorPolicyApplied: 'snapshot',
      });

      await putActionDefinition(db, owner, 'approve', 'runtime callback probe');
      await putBinding(db, {
        kind: 'action',
        owner,
        name: 'approve',
        slot: 'handler',
        bindingId: 'empty-current:approve:handler',
      });
      expectActionReady(
        await om.getBehaviorCatalog(runtime),
        owner,
        'approve',
        'empty-current:approve:handler'
      );
    } finally {
      await om.clearRegistry(runtime);
      db.close();
    }
  });

  test('keeps same-version rollback as a no-op even when a legacy behavior policy is supplied', async () => {
    const { db, om } = await createTestDb();
    const runtime = om.createOmRuntime(db);
    const owner = 'RollbackNoOpOwner';
    const bindingId = 'same-version:approve:handler';

    try {
      await om.defineType(runtime, owner, 'same-version no-op owner');
      await putActionDefinition(db, owner, 'approve', 'current no-op action');
      await putBinding(db, {
        kind: 'action',
        owner,
        name: 'approve',
        slot: 'handler',
        bindingId,
      });
      om.registerAction(runtime, owner, 'approve', bindingId, () => []);

      const currentSnapshot = await om.writeSchemaSnapshot(db, 1, {
        createdAt: '2026-07-18T04:07:00.000Z',
        label: 'same-version no-op behavior v1',
      });
      const legacyCurrentSnapshot = cloneJson(currentSnapshot);
      delete legacyCurrentSnapshot.behavior;
      await putStoredSnapshot(db, legacyCurrentSnapshot);

      const behaviorBefore = await readBehaviorSnapshotRows(db);
      expectActionReady(await om.getBehaviorCatalog(runtime), owner, 'approve', bindingId);

      const result = await om.rollbackSchema(runtime, 1, {
        legacyBehaviorPolicy: 'clear',
      });

      expect(result).toMatchObject({
        ok: true,
        strict: true,
        targetVersion: 1,
        fromVersion: 1,
        diagnostics: [],
        compatibilityDiagnostics: [],
        behaviorPolicyApplied: null,
      });
      expect(await readBehaviorSnapshotRows(db)).toEqual(behaviorBefore);
      expectActionReady(await om.getBehaviorCatalog(runtime), owner, 'approve', bindingId);
    } finally {
      await om.clearRegistry(runtime);
      db.close();
    }
  });

  test('rejects a legacy missing behavior section by default with zero persistent, runtime, state, and cache effects', async () => {
    const { db, om } = await createTestDb();
    const runtime = om.createOmRuntime(db);
    const owner = 'RollbackLegacyDefaultOwner';

    try {
      await om.defineType(runtime, owner, 'legacy default owner');
      await om.defineType(db, 'CacheCanonicalV1', 'cache target in v1');
      const target = await om.writeSchemaSnapshot(db, 1, {
        createdAt: '2026-07-18T04:10:00.000Z',
        label: 'legacy behavior missing v1',
      });
      const legacyTarget = cloneJson(target);
      delete legacyTarget.behavior;
      await putStoredSnapshot(db, legacyTarget);

      await applyRollbackVersionBump(
        db,
        om,
        'behavior-rollback-legacy-default',
        'LegacyDefaultCurrentOnlyType'
      );
      await om.defineType(db, 'CacheCanonicalV2', 'cache target in v2');
      await om.defineTypeAlias(db, 'CachedAlias', 'CacheCanonicalV2');
      expect(await om.resolveType(db, 'CachedAlias')).toBe('CacheCanonicalV2');

      const currentBehavior = rollbackCurrentBehavior(owner, 'legacy-default-current');
      await replaceBehaviorSnapshotRows(db, currentBehavior);
      om.registerAction(
        runtime,
        owner,
        'approve',
        'legacy-default-current:approve:handler',
        () => []
      );
      const stateBefore = await om.getSchemaState(db);
      const behaviorBefore = await readBehaviorSnapshotRows(db);
      const catalogBefore = cloneJson(await om.getBehaviorCatalog(runtime));

      const result = await om.rollbackSchema(db, 1);

      expect(result).toMatchObject({
        ok: false,
        strict: true,
        targetVersion: 1,
        fromVersion: 2,
        diagnostics: [],
        behaviorPolicyApplied: null,
      });
      expect(result.compatibilityDiagnostics).toEqual([
        expect.objectContaining({
          code: 'OMSV1001',
          path: '$.behavior',
          allowedPolicies: ['preserve', 'clear'],
        }),
      ]);
      expect(result.compatibilityDiagnostics[0].message).toMatch(/missing.*behavior/i);

      expect(await om.getSchemaState(db)).toEqual(stateBefore);
      expect(await typeExists(db, 'LegacyDefaultCurrentOnlyType')).toBe(true);
      expect(await readBehaviorSnapshotRows(db)).toEqual(behaviorBefore);
      expect(cloneJson(await om.getBehaviorCatalog(runtime))).toEqual(catalogBefore);
      expect(await om.resolveType(db, 'CachedAlias')).toBe('CacheCanonicalV2');
    } finally {
      await om.clearRegistry(runtime);
      db.close();
    }
  });

  test('applies explicit legacy preserve and clear policies with effective checksums and unchanged runtime registry', async () => {
    async function runPolicyCase(policy) {
      const { db, om } = await createTestDb();
      const runtime = om.createOmRuntime(db);
      const owner = `RollbackLegacy${policy}Owner`;

      try {
        await om.defineType(runtime, owner, `${policy} policy owner`);
        const target = await om.writeSchemaSnapshot(db, 1, {
          createdAt: `2026-07-18T04:${policy === 'preserve' ? '15' : '20'}:00.000Z`,
          label: `legacy ${policy} behavior missing v1`,
        });
        const legacyTarget = cloneJson(target);
        delete legacyTarget.behavior;
        await putStoredSnapshot(db, legacyTarget);

        await applyRollbackVersionBump(
          db,
          om,
          `behavior-rollback-legacy-${policy}`,
          `Legacy${policy}CurrentOnlyType`
        );
        const currentBehavior = rollbackCurrentBehavior(owner, `${policy}-current`);
        await replaceBehaviorSnapshotRows(db, currentBehavior);
        om.registerAction(
          runtime,
          owner,
          'approve',
          `${policy}-current:approve:handler`,
          () => []
        );
        expectActionReady(
          await om.getBehaviorCatalog(runtime),
          owner,
          'approve',
          `${policy}-current:approve:handler`
        );

        const result = await om.rollbackSchema(db, 1, {
          legacyBehaviorPolicy: policy,
        });

        const expectedBehavior =
          policy === 'preserve' ? currentBehavior : behaviorSnapshot();
        expect(await readBehaviorSnapshotRows(db)).toEqual(expectedBehavior);
        expect(await typeExists(db, `Legacy${policy}CurrentOnlyType`)).toBe(false);
        expect(result).toMatchObject({
          ok: true,
          strict: true,
          targetVersion: 1,
          fromVersion: 2,
          diagnostics: [],
          behaviorPolicyApplied: policy,
        });
        expect(result.compatibilityDiagnostics).toEqual([
          expect.objectContaining({
            code: 'OMSV1001',
            path: '$.behavior',
          }),
        ]);
        expect(await om.getSchemaState(db)).toEqual({
          currentVersion: 1,
          checksum: schemaChecksum(
            snapshotWithResolvedBehavior(legacyTarget, expectedBehavior)
          ),
        });

        if (policy === 'preserve') {
          expectActionReady(
            await om.getBehaviorCatalog(runtime),
            owner,
            'approve',
            `${policy}-current:approve:handler`
          );
        } else {
          await putActionDefinition(db, owner, 'approve', 'clear runtime callback probe');
          await putBinding(db, {
            kind: 'action',
            owner,
            name: 'approve',
            slot: 'handler',
            bindingId: `${policy}-current:approve:handler`,
          });
          expectActionReady(
            await om.getBehaviorCatalog(runtime),
            owner,
            'approve',
            `${policy}-current:approve:handler`
          );
        }
      } finally {
        await om.clearRegistry(runtime);
        db.close();
      }
    }

    await runPolicyCase('preserve');
    await runPolicyCase('clear');
  });

  test('keeps rollback atomic when a behavior relation replace fails after partial restore work', async () => {
    const { db, om } = await createTestDb();
    const runtime = om.createOmRuntime(db);
    const owner = 'RollbackFailureRelationOwner';
    const attemptedReplaces = [];
    const marker = 'injected behavior relation replace failure';

    try {
      const probe = await prepareRollbackFailureProbe(
        db,
        om,
        runtime,
        owner,
        'RelationMid'
      );
      const wrapped = wrappingDb(db, {
        beforeRun(transactionNumber, script) {
          if (transactionNumber !== 1) return;
          const relation = replacedRelationName(script);
          if (!relation) return;
          attemptedReplaces.push(relation);
          if (relation === 'om_interceptor_def') throw new Error(marker);
        },
      });
      const before = await captureRollbackFailurePrestate(db, om, runtime, wrapped, probe);

      const error = await captureRejected(om.rollbackSchema(wrapped, 1));

      expect(error.message).toContain(marker);
      expect(attemptedReplaces).toEqual(
        expect.arrayContaining([
          'om_type',
          'om_attr_def',
          'om_rel_def',
          'om_constraint_def',
          'om_computed_def',
          'om_action_def',
          'om_mutation_def',
          'om_interceptor_def',
        ])
      );
      expect(attemptedReplaces.indexOf('om_mutation_def')).toBeLessThan(
        attemptedReplaces.indexOf('om_interceptor_def')
      );
      await expectRollbackFailurePreserved(db, om, runtime, wrapped, probe, before);
    } finally {
      await om.clearRegistry(runtime);
      db.close();
    }
  });

  test('keeps rollback atomic when schema state write fails after behavior restore', async () => {
    const { db, om } = await createTestDb();
    const runtime = om.createOmRuntime(db);
    const owner = 'RollbackFailureStateOwner';
    const attemptedReplaces = [];
    let stateWriteAttempted = false;
    const marker = 'injected om_schema_state write failure';

    try {
      const probe = await prepareRollbackFailureProbe(
        db,
        om,
        runtime,
        owner,
        'StateWrite'
      );
      const wrapped = wrappingDb(db, {
        beforeRun(transactionNumber, script) {
          if (transactionNumber !== 1) return;
          const relation = replacedRelationName(script);
          if (relation) attemptedReplaces.push(relation);
          if (String(script || '').includes(':put om_schema_state')) {
            stateWriteAttempted = true;
            throw new Error(marker);
          }
        },
      });
      const before = await captureRollbackFailurePrestate(db, om, runtime, wrapped, probe);

      const error = await captureRejected(om.rollbackSchema(wrapped, 1));

      expect(error.message).toContain(marker);
      expect(stateWriteAttempted).toBe(true);
      for (const relation of BEHAVIOR_RELATIONS) {
        expect(attemptedReplaces).toContain(relation);
      }
      await expectRollbackFailurePreserved(db, om, runtime, wrapped, probe, before);
    } finally {
      await om.clearRegistry(runtime);
      db.close();
    }
  });

  test('keeps rollback atomic when transaction commit fails', async () => {
    const { db, om } = await createTestDb();
    const runtime = om.createOmRuntime(db);
    const owner = 'RollbackFailureCommitOwner';
    const attemptedReplaces = [];
    let stateWriteAttempted = false;
    let commitAttempted = false;
    const marker = 'injected rollback commit failure';

    try {
      const probe = await prepareRollbackFailureProbe(
        db,
        om,
        runtime,
        owner,
        'Commit'
      );
      const wrapped = wrappingDb(db, {
        beforeRun(transactionNumber, script) {
          if (transactionNumber !== 1) return;
          const relation = replacedRelationName(script);
          if (relation) attemptedReplaces.push(relation);
          if (String(script || '').includes(':put om_schema_state')) {
            stateWriteAttempted = true;
          }
        },
        beforeCommit(transactionNumber) {
          if (transactionNumber !== 1) return;
          commitAttempted = true;
          throw new Error(marker);
        },
      });
      const before = await captureRollbackFailurePrestate(db, om, runtime, wrapped, probe);

      const error = await captureRejected(om.rollbackSchema(wrapped, 1));

      expect(error.message).toContain(marker);
      expect(stateWriteAttempted).toBe(true);
      expect(commitAttempted).toBe(true);
      for (const relation of BEHAVIOR_RELATIONS) {
        expect(attemptedReplaces).toContain(relation);
      }
      await expectRollbackFailurePreserved(db, om, runtime, wrapped, probe, before);
    } finally {
      await om.clearRegistry(runtime);
      db.close();
    }
  });
});

describe('OM behavior schema versioning rollback runtime gate contract', () => {
  test('queues same-runtime catalog readers until rollback commits, then reprojects readiness', async () => {
    const { db, om } = await createTestDb();
    const owner = 'RollbackGateCatalogOwner';
    const blocker = createRollbackTransactionBlocker();
    let rollbackReleased = false;
    let catalogReadAttemptedBeforeRollbackRelease = false;
    const wrapped = wrappingDb(db, {
      ...blocker.hooks,
      beforeDirectRun(script) {
        if (
          blocker.delayed &&
          !rollbackReleased &&
          String(script || '').trim()
        ) {
          catalogReadAttemptedBeforeRollbackRelease = true;
        }
      },
    });
    const runtime = om.createOmRuntime(wrapped);
    let rollback;
    let reader;

    try {
      const target = await prepareRollbackGateProbe(
        om,
        db,
        owner,
        'behavior-rollback-gate-catalog',
        'gate-current'
      );
      om.registerAction(
        runtime,
        owner,
        'approve',
        'gate-current:approve:handler',
        () => []
      );
      expectActionReady(
        await om.getBehaviorCatalog(runtime),
        owner,
        'approve',
        'gate-current:approve:handler'
      );

      blocker.arm();
      rollback = om.rollbackSchema(runtime, 1);
      await blocker.entered;
      expect(blocker.delayed).toBe(true);

      reader = om.getBehaviorCatalog(runtime);
      await Bun.sleep(20);
      expect(catalogReadAttemptedBeforeRollbackRelease).toBe(false);

      rollbackReleased = true;
      blocker.release();
      const rollbackOutcome = await promiseOutcome(rollback);
      expect(rollbackOutcome.status).toBe('fulfilled');
      expect(rollbackOutcome.value).toMatchObject({
        ok: true,
        behaviorPolicyApplied: 'snapshot',
      });
      expect(await readBehaviorSnapshotRows(db)).toEqual(target.behavior);
      const readerOutcome = await promiseOutcome(reader);
      expect(readerOutcome.status).toBe('fulfilled');
      const projected = catalogEntry(readerOutcome.value, 'action', owner, 'approve');
      expect(projected.callbacks).toEqual([{
        slot: 'handler',
        bindingId: 'v1:approve:handler',
        readiness: 'unresolved',
      }]);
    } finally {
      rollbackReleased = true;
      blocker.release();
      await settleQuietly(reader);
      await settleQuietly(rollback);
      await settleQuietly(om.clearRegistry(runtime));
      db.close();
    }
  });

  test('queues new same-runtime action scope capture and fails closed after rollback', async () => {
    const { db, om } = await createTestDb();
    const owner = 'RollbackGateExecuteOwner';
    const blocker = createRollbackTransactionBlocker();
    let rollbackReleased = false;
    let executionReadAttemptedBeforeRollbackRelease = false;
    const wrapped = wrappingDb(db, {
      ...blocker.hooks,
      beforeDirectRun(script) {
        if (blocker.delayed && !rollbackReleased && String(script || '').trim()) {
          executionReadAttemptedBeforeRollbackRelease = true;
        }
      },
    });
    const runtime = om.createOmRuntime(wrapped);
    let currentHandlerCalls = 0;
    let rollback;
    let execution;

    try {
      await prepareRollbackGateProbe(
        om,
        db,
        owner,
        'behavior-rollback-gate-execute',
        'gate-current'
      );
      om.registerAction(
        runtime,
        owner,
        'approve',
        'gate-current:approve:handler',
        () => {
          currentHandlerCalls++;
          return [];
        }
      );

      blocker.arm();
      rollback = om.rollbackSchema(runtime, 1);
      await blocker.entered;

      execution = om.executeAction(runtime, `${owner}:1`, 'approve', {});
      await Bun.sleep(20);
      expect(executionReadAttemptedBeforeRollbackRelease).toBe(false);

      rollbackReleased = true;
      blocker.release();
      const rollbackOutcome = await promiseOutcome(rollback);
      expect(rollbackOutcome.status).toBe('fulfilled');
      const executionOutcome = await promiseOutcome(execution);
      expect(executionOutcome.status).toBe('rejected');
      expect(executionOutcome.reason).toMatchObject({
        name: 'BehaviorUnresolvedError',
        code: 'OMR1001',
        bindingId: 'v1:approve:handler',
      });
      expect(currentHandlerCalls).toBe(0);
    } finally {
      rollbackReleased = true;
      blocker.release();
      await settleQuietly(execution);
      await settleQuietly(rollback);
      await settleQuietly(om.clearRegistry(runtime));
      db.close();
    }
  });

  test('queues contended manifest import behind same-runtime rollback', async () => {
    const { db, om } = await createTestDb();
    const owner = 'RollbackGateImportOwner';
    let importAttemptedBeforeRollbackRelease = false;
    let rollbackReleased = false;
    const blocker = createRollbackTransactionBlocker();
    const wrapped = wrappingDb(db, {
      ...blocker.hooks,
      beforeDirectRun(script) {
        if (blocker.delayed && !rollbackReleased && String(script || '').trim()) {
          importAttemptedBeforeRollbackRelease = true;
        }
      },
      beforeRun(transactionNumber, script, params) {
        const maybeBlock = blocker.hooks.beforeRun(transactionNumber, script, params);
        if (
          blocker.delayed &&
          !rollbackReleased &&
          String(script || '').includes(':put om_action_def')
        ) {
          importAttemptedBeforeRollbackRelease = true;
        }
        return maybeBlock;
      },
    });
    const runtime = om.createOmRuntime(wrapped);
    let rollback;
    let imported;

    try {
      await prepareRollbackGateProbe(
        om,
        db,
        owner,
        'behavior-rollback-gate-import',
        'gate-current'
      );
      om.registerAction(
        runtime,
        owner,
        'approve',
        'gate-current:approve:handler',
        () => []
      );

      blocker.arm();
      rollback = om.rollbackSchema(runtime, 1);
      await blocker.entered;

      const importedBinding = 'gate-import:approve:handler';
      imported = om.importBehaviorManifestJson(
        runtime,
        actionManifest(om, owner, 'approve', importedBinding),
        {
          actions: [{
            bindingId: importedBinding,
            callback: () => [],
          }],
        },
        { requireReady: true }
      );
      await Bun.sleep(20);
      expect(importAttemptedBeforeRollbackRelease).toBe(false);

      rollbackReleased = true;
      blocker.release();
      const rollbackOutcome = await promiseOutcome(rollback);
      expect(rollbackOutcome.status).toBe('fulfilled');
      const importOutcome = await promiseOutcome(imported);
      expect(importOutcome.status).toBe('fulfilled');
      expect(importOutcome.value.applied).toBe(true);
    } finally {
      rollbackReleased = true;
      blocker.release();
      await settleQuietly(imported);
      await settleQuietly(rollback);
      await settleQuietly(om.clearRegistry(runtime));
      db.close();
    }
  });

  test('queues contended clearRegistry behind same-runtime rollback', async () => {
    const { db, om } = await createTestDb();
    const owner = 'RollbackGateClearOwner';
    const blocker = createRollbackTransactionBlocker();
    const wrapped = wrappingDb(db, blocker.hooks);
    const runtime = om.createOmRuntime(wrapped);
    let rollback;

    try {
      await prepareRollbackGateProbe(
        om,
        db,
        owner,
        'behavior-rollback-gate-clear',
        'gate-current'
      );
      om.registerAction(
        runtime,
        owner,
        'approve',
        'gate-current:approve:handler',
        () => []
      );

      blocker.arm();
      rollback = om.rollbackSchema(runtime, 1);
      await blocker.entered;

      const clear = om.clearRegistry(runtime);
      expect(clear).toBeInstanceOf(Promise);
      await expectPromisePending(clear);

      blocker.release();
      await rollback;
      await clear;
      expect(catalogEntry(await om.getBehaviorCatalog(runtime), 'action', owner, 'approve').callbacks[0])
        .toEqual({
          slot: 'handler',
          bindingId: 'v1:approve:handler',
          readiness: 'unresolved',
        });
    } finally {
      blocker.release();
      await settleQuietly(rollback);
      await settleQuietly(om.clearRegistry(runtime));
      db.close();
    }
  });

  test('lets an already captured in-flight action finish while rollback is blocked', async () => {
    const { db, om } = await createTestDb();
    const owner = 'RollbackGateInFlightOwner';
    const blocker = createRollbackTransactionBlocker();
    const wrapped = wrappingDb(db, blocker.hooks);
    const runtime = om.createOmRuntime(wrapped);
    let actionStarted;
    let releaseAction;
    const started = new Promise((resolve) => {
      actionStarted = resolve;
    });
    const release = new Promise((resolve) => {
      releaseAction = resolve;
    });
    let handlerCalls = 0;
    let rollback;
    let inFlight;

    try {
      await prepareRollbackGateProbe(
        om,
        db,
        owner,
        'behavior-rollback-gate-inflight',
        'gate-current'
      );
      om.registerAction(
        runtime,
        owner,
        'approve',
        'gate-current:approve:handler',
        async () => {
          handlerCalls++;
          actionStarted();
          await release;
          return [];
        }
      );

      inFlight = om.executeAction(runtime, `${owner}:1`, 'approve', {});
      await started;

      blocker.arm();
      rollback = om.rollbackSchema(runtime, 1);

      releaseAction();
      await inFlight;
      expect(handlerCalls).toBe(1);

      await blocker.entered;
      blocker.release();
      await rollback;
    } finally {
      releaseAction();
      blocker.release();
      await settleQuietly(inFlight);
      await settleQuietly(rollback);
      await settleQuietly(om.clearRegistry(runtime));
      db.close();
    }
  });

  test('does not queue a second runtime sharing the same DB behind the first runtime rollback gate', async () => {
    const { db, om } = await createTestDb();
    const owner = 'RollbackGateSecondRuntimeOwner';
    const blocker = createRollbackTransactionBlocker();
    let rollbackReleased = false;
    let rightReadAttemptedBeforeRollbackRelease = false;
    const wrapped = wrappingDb(db, {
      ...blocker.hooks,
      beforeDirectRun(script) {
        if (
          blocker.delayed &&
          !rollbackReleased &&
          String(script || '').trim()
        ) {
          rightReadAttemptedBeforeRollbackRelease = true;
        }
      },
    });
    const left = om.createOmRuntime(wrapped);
    const right = om.createOmRuntime(wrapped);
    let rollback;
    let rightCatalog;

    try {
      await prepareRollbackGateProbe(
        om,
        db,
        owner,
        'behavior-rollback-gate-second-runtime',
        'gate-current'
      );
      om.registerAction(
        left,
        owner,
        'approve',
        'gate-current:approve:handler',
        () => []
      );
      om.registerAction(
        right,
        owner,
        'approve',
        'right-only:approve:handler',
        () => []
      );

      blocker.arm();
      rollback = om.rollbackSchema(left, 1);
      await blocker.entered;

      rightCatalog = om.getBehaviorCatalog(right);
      await Bun.sleep(20);
      expect(rightReadAttemptedBeforeRollbackRelease).toBe(true);

      rollbackReleased = true;
      blocker.release();
      const rollbackOutcome = await promiseOutcome(rollback);
      expect(rollbackOutcome.status).toBe('fulfilled');
      const rightOutcome = await promiseOutcome(rightCatalog);
      expect(rightOutcome.status).toBe('fulfilled');
      expect(['gate-current:approve:handler', 'v1:approve:handler']).toContain(
        catalogEntry(rightOutcome.value, 'action', owner, 'approve').callbacks[0].bindingId
      );
      expect(catalogEntry(await om.getBehaviorCatalog(right), 'action', owner, 'approve').callbacks[0])
        .toEqual({
          slot: 'handler',
          bindingId: 'v1:approve:handler',
          readiness: 'unresolved',
      });
    } finally {
      rollbackReleased = true;
      blocker.release();
      await settleQuietly(rightCatalog);
      await settleQuietly(rollback);
      await settleQuietly(om.clearRegistry(left));
      await settleQuietly(om.clearRegistry(right));
      db.close();
    }
  });
});

describe('OM behavior schema versioning rollback structural validation contract', () => {
  test('does not call a ready custom validator during rollback strict validation', async () => {
    const { db, om } = await createTestDb();
    const runtime = om.createOmRuntime(db);
    const owner = 'RollbackShapeCustomOwner';
    let validatorCalls = 0;

    try {
      await prepareRollbackConstraintProbe(
        om,
        runtime,
        owner
      );
      await om.defineConstraint(runtime, owner, 'guard', {
        scope: 'custom',
        validator: () => null,
      });
      await putBinding(db, {
        kind: 'constraint',
        owner,
        name: 'guard',
        slot: 'validator',
        bindingId: 'shape:guard:validator',
      });
      om.registerValidator(runtime, owner, 'guard', 'shape:guard:validator', () => {
        validatorCalls++;
        return null;
      });
      await materializeRollbackConstraintTarget(
        om,
        db,
        owner,
        'behavior-rollback-shape-custom'
      );

      const outcome = await promiseOutcome(om.rollbackSchema(runtime, 1), 500);
      expect(outcome.status).toBe('fulfilled');
      expect(outcome.value).toMatchObject({ ok: true });
      expect(validatorCalls).toBe(0);
    } finally {
      await settleQuietly(om.clearRegistry(runtime));
      db.close();
    }
  });

  test('does not call ready conditional when/then callbacks during rollback strict validation', async () => {
    const { db, om } = await createTestDb();
    const runtime = om.createOmRuntime(db);
    const owner = 'RollbackShapeConditionalOwner';
    let whenCalls = 0;
    let thenCalls = 0;

    try {
      await prepareRollbackConstraintProbe(
        om,
        runtime,
        owner
      );
      await om.defineConstraint(runtime, owner, 'has_owner', {
        scope: 'conditional',
        message: 'owner required',
        when: () => true,
        then: () => true,
      });
      await putBinding(db, {
        kind: 'constraint',
        owner,
        name: 'has_owner',
        slot: 'when',
        bindingId: 'shape:has_owner:when',
      });
      await putBinding(db, {
        kind: 'constraint',
        owner,
        name: 'has_owner',
        slot: 'then',
        bindingId: 'shape:has_owner:then',
      });
      om.registerConstraint(
        runtime,
        owner,
        'has_owner',
        'shape:has_owner:when',
        () => {
          whenCalls++;
          return true;
        },
        'shape:has_owner:then',
        () => {
          thenCalls++;
          return true;
        }
      );
      await materializeRollbackConstraintTarget(
        om,
        db,
        owner,
        'behavior-rollback-shape-conditional'
      );

      const outcome = await promiseOutcome(om.rollbackSchema(runtime, 1), 500);
      expect(outcome.status).toBe('fulfilled');
      expect(outcome.value).toMatchObject({ ok: true });
      expect(whenCalls).toBe(0);
      expect(thenCalls).toBe(0);
    } finally {
      await settleQuietly(om.clearRegistry(runtime));
      db.close();
    }
  });

  test('does not deadlock rollback on a reentrant validator body because it is not executed', async () => {
    const { db, om } = await createTestDb();
    const runtime = om.createOmRuntime(db);
    const owner = 'RollbackShapeReentrantOwner';
    let validatorCalls = 0;

    try {
      await prepareRollbackConstraintProbe(
        om,
        runtime,
        owner
      );
      await om.defineConstraint(runtime, owner, 'catalog_guard', {
        scope: 'custom',
        validator: () => null,
      });
      await putBinding(db, {
        kind: 'constraint',
        owner,
        name: 'catalog_guard',
        slot: 'validator',
        bindingId: 'shape:catalog_guard:validator',
      });
      om.registerValidator(
        runtime,
        owner,
        'catalog_guard',
        'shape:catalog_guard:validator',
        async (ctx) => {
          validatorCalls++;
          await om.getBehaviorCatalog(ctx.runtime);
          return null;
        }
      );
      await materializeRollbackConstraintTarget(
        om,
        db,
        owner,
        'behavior-rollback-shape-reentrant'
      );

      const outcome = await promiseOutcome(om.rollbackSchema(runtime, 1), 500);
      expect(outcome.status).toBe('fulfilled');
      expect(outcome.value).toMatchObject({ ok: true });
      expect(validatorCalls).toBe(0);
    } finally {
      await settleQuietly(om.clearRegistry(runtime));
      db.close();
    }
  });

  test('keeps public validateEntity executing custom and conditional callbacks after rollback', async () => {
    const { db, om } = await createTestDb();
    const runtime = om.createOmRuntime(db);
    const owner = 'RollbackShapePublicValidateOwner';
    let validatorCalls = 0;
    let whenCalls = 0;
    let thenCalls = 0;

    try {
      await prepareRollbackConstraintProbe(
        om,
        runtime,
        owner
      );
      await om.defineConstraint(runtime, owner, 'guard', {
        scope: 'custom',
        validator: () => null,
      });
      await om.defineConstraint(runtime, owner, 'has_owner', {
        scope: 'conditional',
        message: 'owner required',
        when: () => true,
        then: () => true,
      });
      await putBinding(db, {
        kind: 'constraint',
        owner,
        name: 'guard',
        slot: 'validator',
        bindingId: 'shape-public:guard:validator',
      });
      await putBinding(db, {
        kind: 'constraint',
        owner,
        name: 'has_owner',
        slot: 'when',
        bindingId: 'shape-public:has_owner:when',
      });
      await putBinding(db, {
        kind: 'constraint',
        owner,
        name: 'has_owner',
        slot: 'then',
        bindingId: 'shape-public:has_owner:then',
      });
      om.registerValidator(
        runtime,
        owner,
        'guard',
        'shape-public:guard:validator',
        () => {
          validatorCalls++;
          return null;
        }
      );
      om.registerConstraint(
        runtime,
        owner,
        'has_owner',
        'shape-public:has_owner:when',
        () => {
          whenCalls++;
          return true;
        },
        'shape-public:has_owner:then',
        () => {
          thenCalls++;
          return true;
        }
      );
      await materializeRollbackConstraintTarget(
        om,
        db,
        owner,
        'behavior-rollback-shape-public-validate'
      );

      const rollbackOutcome = await promiseOutcome(om.rollbackSchema(runtime, 1), 500);
      expect(rollbackOutcome.status).toBe('fulfilled');
      expect(rollbackOutcome.value).toMatchObject({ ok: true });
      expect({ validatorCalls, whenCalls, thenCalls }).toEqual({
        validatorCalls: 0,
        whenCalls: 0,
        thenCalls: 0,
      });

      const validation = await om.validateEntity(runtime, `${owner}:1`);
      expect(validation).toEqual({ valid: true, errors: [] });
      expect({ validatorCalls, whenCalls, thenCalls }).toEqual({
        validatorCalls: 1,
        whenCalls: 1,
        thenCalls: 1,
      });
    } finally {
      await settleQuietly(om.clearRegistry(runtime));
      db.close();
    }
  });
});
