const { describe, expect, test } = require('bun:test');

const { CozoDb } = require('../index');
const om = require('../cozo-om');

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

async function putStoredProperty(db, entityId, attrName, value, validTime = 'ASSERT') {
  await db.run(
    `
?[entity_id, attr_name, valid_time, value, tx_time] <-
  [[$entity_id, $attr_name, $valid_time, $value, $tx_time]]
:put om_property {entity_id, attr_name, valid_time => value, tx_time}
    `.trim(),
    {
      entity_id: entityId,
      attr_name: attrName,
      valid_time: validTime,
      value,
      tx_time: new Date().toISOString(),
    }
  );
}

async function expectUnresolved(promise, expected) {
  try {
    await promise;
  } catch (error) {
    expect(error).toBeInstanceOf(om.BehaviorUnresolvedError);
    expect(error.diagnostic).toMatchObject({
      code: 'OMR1001',
      ...expected,
    });
    return error;
  }
  throw new Error('Expected BehaviorUnresolvedError');
}

describe('OM behavior portability readiness gate', () => {
  test('constraint custom validator and partial when/then binding fail closed before callbacks', async () => {
    const db = new CozoDb('mem', '', {});
    const runtime = om.createOmRuntime(db);
    await om.initSchema(runtime);
    try {
      await om.defineType(runtime, 'Resource', 'resource');
      await om.createEntity(runtime, 'resource:constraint', 'Resource', 'Resource');

      const calls = [];
      await om.defineConstraint(runtime, 'Resource', 'guard', {
        scope: 'conditional',
        when: () => {
          calls.push('when');
          return true;
        },
        then: () => {
          calls.push('then');
          return true;
        },
      });
      await putBinding(db, {
        kind: 'constraint',
        owner: 'Resource',
        name: 'guard',
        slot: 'when',
        bindingId: 'binding:when',
      });
      await putBinding(db, {
        kind: 'constraint',
        owner: 'Resource',
        name: 'guard',
        slot: 'then',
        bindingId: 'binding:then',
      });
      om.registerConstraint(
        runtime,
        'Resource',
        'guard',
        'binding:when',
        () => {
          calls.push('registered-when');
          return true;
        },
        'wrong:then',
        () => {
          calls.push('registered-then');
          return true;
        }
      );

      await expectUnresolved(om.validateConstraints(runtime, 'resource:constraint'), {
        kind: 'constraint',
        ownerType: 'Resource',
        behaviorKey: 'constraint:Resource/guard',
        slot: 'then',
        bindingId: 'binding:then',
        interceptorPhase: null,
        interceptorSeq: null,
      });
      expect(calls).toEqual([]);

      await om.defineConstraint(runtime, 'Resource', 'custom_guard', {
        scope: 'custom',
        validator: () => {
          calls.push('legacy-validator');
          return null;
        },
      });
      await putBinding(db, {
        kind: 'constraint',
        owner: 'Resource',
        name: 'custom_guard',
        slot: 'validator',
        bindingId: 'binding:validator',
      });
      om.registerValidator(runtime, 'Resource', 'custom_guard', 'wrong:validator', () => {
        calls.push('registered-validator');
        return null;
      });

      await expectUnresolved(om.validateConstraints(runtime, 'resource:constraint'), {
        kind: 'constraint',
        ownerType: 'Resource',
        behaviorKey: 'constraint:Resource/custom_guard',
        slot: 'validator',
        bindingId: 'binding:validator',
        interceptorPhase: null,
        interceptorSeq: null,
      });
      expect(calls).toEqual([]);
    } finally {
      db.close();
      om.clearRegistry(runtime);
    }
  });

  test('computed readiness is checked before stored values and survives clear/rebind', async () => {
    const db = new CozoDb('mem', '', {});
    const runtime = om.createOmRuntime(db);
    await om.initSchema(runtime);
    try {
      await om.defineType(runtime, 'Resource', 'resource');
      await om.defineAttribute(runtime, 'Resource', 'score', 'Number', false);
      await om.defineComputed(runtime, 'Resource', 'score', () => 99);
      await om.createEntity(runtime, 'resource:computed', 'Resource', 'Resource');
      await putStoredProperty(db, 'resource:computed', 'score', 7);
      await putBinding(db, {
        kind: 'computed',
        owner: 'Resource',
        name: 'score',
        slot: 'compute',
        bindingId: 'binding:compute',
      });

      await expectUnresolved(om.getProperty(runtime, 'resource:computed', 'score'), {
        kind: 'computed',
        ownerType: 'Resource',
        behaviorKey: 'computed:Resource/score',
        slot: 'compute',
        bindingId: 'binding:compute',
        interceptorPhase: null,
        interceptorSeq: null,
      });
      await expectUnresolved(
        om.getPropertyAsOf(runtime, 'resource:computed', 'score', '2026-01-01T00:00:00Z'),
        { kind: 'computed', ownerType: 'Resource', behaviorKey: 'computed:Resource/score' }
      );
      await expectUnresolved(om.getEntityView(runtime, 'resource:computed'), {
        kind: 'computed',
        ownerType: 'Resource',
        behaviorKey: 'computed:Resource/score',
      });
      await putStoredProperty(
        db,
        'resource:computed',
        'score',
        8,
        '2025-01-01T00:00:00Z'
      );
      await expectUnresolved(
        om.getEntityViewAsOf(runtime, 'resource:computed', '2026-01-01T00:00:00Z'),
        {
          kind: 'computed',
          ownerType: 'Resource',
          behaviorKey: 'computed:Resource/score',
          slot: 'compute',
          bindingId: 'binding:compute',
        }
      );

      let computeCalls = 0;
      om.registerComputed(runtime, 'Resource', 'score', 'binding:compute', () => {
        computeCalls += 1;
        return 99;
      });
      expect(await om.getProperty(runtime, 'resource:computed', 'score')).toBe(7);
      expect((await om.getEntityView(runtime, 'resource:computed')).properties.score).toBe(7);
      expect(
        (await om.getEntityViewAsOf(
          runtime,
          'resource:computed',
          '2026-01-01T00:00:00Z'
        )).properties.score
      ).toBe(8);
      expect(computeCalls).toBe(0);

      om.clearRegistry(runtime);
      await expectUnresolved(om.getProperty(runtime, 'resource:computed', 'score'), {
        kind: 'computed',
        ownerType: 'Resource',
        behaviorKey: 'computed:Resource/score',
      });
      om.registerComputed(runtime, 'Resource', 'score', 'binding:compute', () => 100);
      expect(await om.getProperty(runtime, 'resource:computed', 'score')).toBe(7);
    } finally {
      db.close();
      om.clearRegistry(runtime);
    }
  });

  test('parent action resolves at the call site and shares the action transaction', async () => {
    const db = new CozoDb('mem', '', {});
    const runtime = om.createOmRuntime(db);
    await om.initSchema(runtime);
    try {
      await om.defineType(runtime, 'Parent', 'parent');
      await om.defineType(runtime, 'Child', 'child', { parentType: 'Parent' });
      await om.defineAttribute(runtime, 'Parent', 'status', 'String', false);
      await om.createEntity(runtime, 'child:parent-action', 'Child', 'Child');
      await om.setProperty(runtime, 'child:parent-action', 'status', 'initial');

      const calls = [];
      await om.defineAction(runtime, 'Parent', 'go', () => {
        calls.push('parent');
        return [];
      });
      await om.defineAction(runtime, 'Child', 'go', async (ctx) => {
        calls.push('child');
        await ctx.setProperty('status', 'child');
        return ctx.callParentAction('go');
      });
      await om.addInterceptor(runtime, 'Parent', 'go', 'before', async (ctx) => {
        calls.push('before');
        await ctx.setProperty('status', 'before');
      });
      await putBinding(db, {
        kind: 'action',
        owner: 'Parent',
        name: 'go',
        slot: 'handler',
        bindingId: 'binding:parent-go',
      });
      om.registerAction(runtime, 'Parent', 'go', 'wrong:parent-go', () => {
        calls.push('registered-parent-wrong');
        return [];
      });

      await expectUnresolved(om.executeAction(runtime, 'child:parent-action', 'go'), {
        kind: 'action',
        ownerType: 'Parent',
        behaviorKey: 'action:Parent/go',
        slot: 'handler',
        bindingId: 'binding:parent-go',
        interceptorPhase: null,
        interceptorSeq: null,
      });
      expect(calls).toEqual(['before', 'child']);
      expect(await om.getProperty(runtime, 'child:parent-action', 'status')).toBe('initial');

      calls.length = 0;
      await om.defineAction(runtime, 'Child', 'go', async (ctx) => {
        calls.push('child-no-parent');
        await ctx.setProperty('status', 'committed');
        return [];
      });
      await om.executeAction(runtime, 'child:parent-action', 'go');
      expect(calls).toEqual(['before', 'child-no-parent']);
      expect(await om.getProperty(runtime, 'child:parent-action', 'status')).toBe('committed');

      calls.length = 0;
      await om.defineAction(runtime, 'Child', 'go', async (ctx) => {
        calls.push('child');
        return ctx.callParentAction('go');
      });
      om.registerAction(runtime, 'Parent', 'go', 'binding:parent-go', () => {
        calls.push('parent');
        return [];
      });
      await om.executeAction(runtime, 'child:parent-action', 'go');
      expect(calls).toEqual(['before', 'child', 'parent']);
    } finally {
      db.close();
      om.clearRegistry(runtime);
    }
  });

  test('unresolved child action definition does not fall back to parent action', async () => {
    const db = new CozoDb('mem', '', {});
    const runtime = om.createOmRuntime(db);
    await om.initSchema(runtime);
    try {
      await om.defineType(runtime, 'Parent', 'parent');
      await om.defineType(runtime, 'Child', 'child', { parentType: 'Parent' });
      await om.createEntity(runtime, 'child:action', 'Child', 'Child');
      const calls = [];
      await om.defineAction(runtime, 'Parent', 'deploy', () => {
        calls.push('parent-action');
        return [];
      });
      await om.defineAction(runtime, 'Child', 'deploy', () => {
        calls.push('child-action');
        return [];
      });
      await putBinding(db, {
        kind: 'action',
        owner: 'Child',
        name: 'deploy',
        slot: 'handler',
        bindingId: 'binding:child-action',
      });

      await expectUnresolved(om.executeAction(runtime, 'child:action', 'deploy'), {
        kind: 'action',
        ownerType: 'Child',
        behaviorKey: 'action:Child/deploy',
        slot: 'handler',
        bindingId: 'binding:child-action',
        interceptorPhase: null,
        interceptorSeq: null,
      });
      expect(calls).toEqual([]);
    } finally {
      db.close();
      om.clearRegistry(runtime);
    }
  });

  test('direct and action-returned mutation batches resolve before the first mutation callback', async () => {
    const db = new CozoDb('mem', '', {});
    const runtime = om.createOmRuntime(db);
    await om.initSchema(runtime);
    try {
      await om.defineType(runtime, 'Resource', 'resource');
      await om.defineAttribute(runtime, 'Resource', 'status', 'String', false);
      await om.createEntity(runtime, 'resource:mutation', 'Resource', 'Resource');
      await om.setProperty(runtime, 'resource:mutation', 'status', 'initial');

      const calls = [];
      await om.defineMutation(runtime, 'Resource', 'first', async (ctx) => {
        calls.push('first');
        await ctx.setProperty('status', 'first');
      });
      await om.defineMutation(runtime, 'Resource', 'second', async (ctx) => {
        calls.push('second');
        await ctx.setProperty('status', 'second');
      });
      await putBinding(db, {
        kind: 'mutation',
        owner: 'Resource',
        name: 'first',
        slot: 'executor',
        bindingId: 'binding:first',
      });
      await putBinding(db, {
        kind: 'mutation',
        owner: 'Resource',
        name: 'second',
        slot: 'executor',
        bindingId: 'binding:second',
      });
      om.registerMutation(runtime, 'Resource', 'first', 'binding:first', async (ctx) => {
        calls.push('registered-first');
        await ctx.setProperty('status', 'first');
      });
      om.registerMutation(runtime, 'Resource', 'second', 'wrong:second', async (ctx) => {
        calls.push('registered-second');
        await ctx.setProperty('status', 'second');
      });

      const batch = [{ mutation: 'first' }, { mutation: 'second' }];
      await expectUnresolved(om.executeMutations(runtime, 'resource:mutation', batch), {
        kind: 'mutation',
        ownerType: 'Resource',
        behaviorKey: 'mutation:Resource/second',
        slot: 'executor',
        bindingId: 'binding:second',
      });
      expect(calls).toEqual([]);
      expect(await om.getProperty(runtime, 'resource:mutation', 'status')).toBe('initial');

      await om.defineAction(runtime, 'Resource', 'runBatch', () => {
        calls.push('action');
        return batch;
      });
      await expectUnresolved(om.executeAction(runtime, 'resource:mutation', 'runBatch'), {
        kind: 'mutation',
        ownerType: 'Resource',
        behaviorKey: 'mutation:Resource/second',
        slot: 'executor',
        bindingId: 'binding:second',
      });
      expect(calls).toEqual(['action']);
      expect(await om.getProperty(runtime, 'resource:mutation', 'status')).toBe('initial');
    } finally {
      db.close();
      om.clearRegistry(runtime);
    }
  });

  test('after interceptor readiness is resolved before inherited before interceptors or action', async () => {
    const db = new CozoDb('mem', '', {});
    const runtime = om.createOmRuntime(db);
    await om.initSchema(runtime);
    try {
      await om.defineType(runtime, 'Parent', 'parent');
      await om.defineType(runtime, 'Child', 'child', { parentType: 'Parent' });
      await om.defineAttribute(runtime, 'Parent', 'status', 'String', false);
      await om.createEntity(runtime, 'child:interceptor', 'Child', 'Child');
      await om.setProperty(runtime, 'child:interceptor', 'status', 'initial');

      const calls = [];
      await om.defineAction(runtime, 'Child', 'change', () => {
        calls.push('action');
        return [];
      });
      await om.addInterceptor(runtime, 'Parent', 'change', 'before', () => {
        calls.push('parent-before');
      });
      await om.addInterceptor(runtime, 'Parent', 'change', 'after', () => {
        calls.push('parent-after');
      });
      await putBinding(db, {
        kind: 'interceptor',
        owner: 'Parent',
        name: 'change',
        slot: 'handler',
        phase: 'before',
        seq: 0,
        bindingId: 'binding:before',
      });
      await putBinding(db, {
        kind: 'interceptor',
        owner: 'Parent',
        name: 'change',
        slot: 'handler',
        phase: 'after',
        seq: 0,
        bindingId: 'binding:after',
      });
      om.registerInterceptor(
        runtime,
        'Parent',
        'change',
        'before',
        0,
        'binding:before',
        () => {
          calls.push('registered-before');
        }
      );
      om.registerInterceptor(
        runtime,
        'Parent',
        'change',
        'after',
        0,
        'wrong:after',
        () => {
          calls.push('registered-after');
        }
      );

      await expectUnresolved(om.executeAction(runtime, 'child:interceptor', 'change'), {
        kind: 'interceptor',
        ownerType: 'Parent',
        behaviorKey: 'interceptor:Parent/change/after/0',
        slot: 'handler',
        bindingId: 'binding:after',
        interceptorPhase: 'after',
        interceptorSeq: 0,
      });
      expect(calls).toEqual([]);
      expect(await om.getProperty(runtime, 'child:interceptor', 'status')).toBe('initial');

      om.registerInterceptor(
        runtime,
        'Parent',
        'change',
        'before',
        0,
        'wrong:before',
        () => {
          calls.push('wrong-before');
        }
      );
      om.registerInterceptor(
        runtime,
        'Parent',
        'change',
        'after',
        0,
        'binding:after',
        () => {
          calls.push('ready-after');
        }
      );

      await expectUnresolved(om.executeAction(runtime, 'child:interceptor', 'change'), {
        kind: 'interceptor',
        ownerType: 'Parent',
        behaviorKey: 'interceptor:Parent/change/before/0',
        slot: 'handler',
        bindingId: 'binding:before',
        interceptorPhase: 'before',
        interceptorSeq: 0,
      });
      expect(calls).toEqual([]);
      expect(await om.getProperty(runtime, 'child:interceptor', 'status')).toBe('initial');
    } finally {
      db.close();
      om.clearRegistry(runtime);
    }
  });

  test('constraint write paths fail closed before callbacks and roll back writes', async () => {
    const db = new CozoDb('mem', '', {});
    const runtime = om.createOmRuntime(db);
    await om.initSchema(runtime);
    try {
      await om.defineType(runtime, 'Employee', 'employee');
      await om.defineAttribute(runtime, 'Employee', 'status', 'String', false);
      await om.defineAttribute(runtime, 'Employee', 'end_date', 'String', false);
      await om.createEntity(runtime, 'employee:write', 'Employee', 'Employee');
      await om.setProperty(runtime, 'employee:write', 'status', 'active');

      const propertyCalls = [];
      await om.defineConstraint(runtime, 'Employee', 'active_has_no_end_date', {
        scope: 'conditional',
        when: async () => {
          propertyCalls.push('when');
          return true;
        },
        then: async () => {
          propertyCalls.push('then');
          return true;
        },
      });
      await putBinding(db, {
        kind: 'constraint',
        owner: 'Employee',
        name: 'active_has_no_end_date',
        slot: 'when',
        bindingId: 'binding:property-when',
      });
      await putBinding(db, {
        kind: 'constraint',
        owner: 'Employee',
        name: 'active_has_no_end_date',
        slot: 'then',
        bindingId: 'binding:property-then',
      });
      om.registerConstraint(
        runtime,
        'Employee',
        'active_has_no_end_date',
        'binding:property-when',
        async () => {
          propertyCalls.push('registered-when');
          return true;
        },
        'wrong:property-then',
        async () => {
          propertyCalls.push('registered-then');
          return true;
        }
      );

      await expectUnresolved(
        om.setProperty(runtime, 'employee:write', 'end_date', '2026-12-31'),
        {
          kind: 'constraint',
          ownerType: 'Employee',
          behaviorKey: 'constraint:Employee/active_has_no_end_date',
          slot: 'then',
          bindingId: 'binding:property-then',
        }
      );
      expect(propertyCalls).toEqual([]);
      expect(await om.getProperty(runtime, 'employee:write', 'end_date')).toBeUndefined();

      om.registerConstraint(
        runtime,
        'Employee',
        'active_has_no_end_date',
        'binding:property-when',
        async () => {
          propertyCalls.push('ready-when');
          return true;
        },
        'binding:property-then',
        async () => {
          propertyCalls.push('ready-then');
          return true;
        }
      );
      await om.setProperty(runtime, 'employee:write', 'end_date', '2026-12-31');
      expect(await om.getProperty(runtime, 'employee:write', 'end_date')).toBe('2026-12-31');

      await om.defineType(runtime, 'Department', 'department');
      await om.defineRelation(runtime, 'heads', 'Department', 'Employee', true);
      await om.createEntity(runtime, 'department:write', 'Department', 'Department');
      await om.createEntity(runtime, 'employee:head', 'Employee', 'Head');

      const linkCalls = [];
      await om.defineConstraint(runtime, 'Department', 'head_guard', {
        scope: 'cross-entity',
        when: async () => {
          linkCalls.push('when');
          return true;
        },
        then: async () => {
          linkCalls.push('then');
          return true;
        },
      });
      await putBinding(db, {
        kind: 'constraint',
        owner: 'Department',
        name: 'head_guard',
        slot: 'when',
        bindingId: 'binding:link-when',
      });
      await putBinding(db, {
        kind: 'constraint',
        owner: 'Department',
        name: 'head_guard',
        slot: 'then',
        bindingId: 'binding:link-then',
      });
      om.registerConstraint(
        runtime,
        'Department',
        'head_guard',
        'binding:link-when',
        async () => {
          linkCalls.push('registered-when');
          return true;
        },
        'wrong:link-then',
        async () => {
          linkCalls.push('registered-then');
          return true;
        }
      );

      await expectUnresolved(
        om.linkEntities(runtime, 'department:write', 'heads', 'employee:head'),
        {
          kind: 'constraint',
          ownerType: 'Department',
          behaviorKey: 'constraint:Department/head_guard',
          slot: 'then',
          bindingId: 'binding:link-then',
        }
      );
      expect(linkCalls).toEqual([]);
      expect(
        (await om.getNeighbors(runtime, 'department:write', 'heads', 'outgoing')).outgoing
      ).toEqual([]);

      om.registerConstraint(
        runtime,
        'Department',
        'head_guard',
        'binding:link-when',
        async () => {
          linkCalls.push('ready-when');
          return true;
        },
        'binding:link-then',
        async () => {
          linkCalls.push('ready-then');
          return true;
        }
      );
      await om.linkEntities(runtime, 'department:write', 'heads', 'employee:head');
      expect(
        (await om.getNeighbors(runtime, 'department:write', 'heads', 'outgoing')).outgoing
          .map((entry) => entry.entityId)
      ).toEqual(['employee:head']);
    } finally {
      db.close();
      om.clearRegistry(runtime);
    }
  });
});
