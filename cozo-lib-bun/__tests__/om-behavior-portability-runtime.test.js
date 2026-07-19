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

async function putInterceptorDefinition(db, owner, name, phase, seq, description = '') {
  await db.run(
    `
?[type_name, action_name, phase, seq, description] <-
  [[$owner, $name, $phase, $seq, $description]]
:put om_interceptor_def {type_name, action_name, phase, seq => description}
    `.trim(),
    { owner, name, phase, seq, description }
  );
}

function entry(catalog, kind, name, phase = null, seq = null) {
  return catalog.behaviors.find((candidate) =>
    candidate.kind === kind
    && candidate.name === name
    && candidate.interceptorPhase === phase
    && candidate.interceptorSeq === seq
  );
}

describe('OM behavior portability runtime binding', () => {
  test('registers five behavior kinds and six slots with exact, partial, and rebound readiness', async () => {
    const db = new CozoDb('mem', '', {});
    const runtime = om.createOmRuntime(db);
    await om.initSchema(runtime);

    try {
      const calls = [];
      await om.defineType(runtime, 'PortableOwner', 'portable owner');
      await om.createEntity(runtime, 'portable:1', 'PortableOwner', 'Portable 1');
      await om.defineConstraint(runtime, 'PortableOwner', 'conditional_rule', {
        scope: 'conditional',
        when: () => true,
        then: () => true,
      });
      await om.defineConstraint(runtime, 'PortableOwner', 'custom_rule', {
        scope: 'custom',
        validator: () => null,
      });
      await om.defineComputed(runtime, 'PortableOwner', 'computed_value', () => 1);
      await om.defineAction(runtime, 'PortableOwner', 'portable_action', () => []);
      await om.defineAction(runtime, 'PortableOwner', 'legacy_action', () => []);
      await om.defineMutation(runtime, 'PortableOwner', 'portable_mutation', () => {});
      await om.addInterceptor(
        runtime,
        'PortableOwner',
        'portable_action',
        'after',
        () => {}
      );

      const bindings = [
        ['constraint', 'conditional_rule', 'when', '', -1, 'id:when'],
        ['constraint', 'conditional_rule', 'then', '', -1, 'id:then'],
        ['constraint', 'custom_rule', 'validator', '', -1, 'id:validator'],
        ['computed', 'computed_value', 'compute', '', -1, 'id:computed'],
        ['action', 'portable_action', 'handler', '', -1, 'id:action'],
        ['mutation', 'portable_mutation', 'executor', '', -1, 'id:mutation'],
        ['interceptor', 'portable_action', 'handler', 'after', 0, 'id:interceptor'],
      ];
      for (const [kind, name, slot, phase, seq, bindingId] of bindings) {
        await putBinding(db, {
          kind,
          owner: 'PortableOwner',
          name,
          slot,
          phase,
          seq,
          bindingId,
        });
      }

      om.registerConstraint(
        runtime,
        'PortableOwner',
        'conditional_rule',
        'id:when',
        () => true,
        'wrong:then',
        () => true
      );
      om.registerValidator(
        runtime,
        'PortableOwner',
        'custom_rule',
        'id:validator',
        () => null
      );
      om.registerComputed(
        runtime,
        'PortableOwner',
        'computed_value',
        'id:computed',
        () => {
          calls.push('computed');
          return 2;
        }
      );
      om.registerAction(
        runtime,
        'PortableOwner',
        'portable_action',
        'id:action',
        () => {
          calls.push('action');
          return [];
        }
      );
      om.registerMutation(
        runtime,
        'PortableOwner',
        'portable_mutation',
        'id:mutation',
        () => {
          calls.push('mutation');
        }
      );
      om.registerInterceptor(
        runtime,
        'PortableOwner',
        'portable_action',
        'after',
        0,
        'id:interceptor',
        () => {
          calls.push('interceptor');
        }
      );

      let catalog = await om.getBehaviorCatalog(runtime);
      expect(entry(catalog, 'constraint', 'conditional_rule').callbacks).toEqual([
        { slot: 'when', bindingId: 'id:when', readiness: 'ready' },
        { slot: 'then', bindingId: 'id:then', readiness: 'unresolved' },
      ]);
      expect(entry(catalog, 'constraint', 'custom_rule').callbacks).toEqual([
        { slot: 'validator', bindingId: 'id:validator', readiness: 'ready' },
      ]);
      expect(entry(catalog, 'computed', 'computed_value').callbacks[0].readiness).toBe('ready');
      expect(entry(catalog, 'action', 'portable_action').callbacks[0].readiness).toBe('ready');
      expect(entry(catalog, 'mutation', 'portable_mutation').callbacks[0].readiness).toBe('ready');
      expect(
        entry(catalog, 'interceptor', 'portable_action', 'after', 0).callbacks[0].readiness
      ).toBe('ready');
      expect(entry(catalog, 'action', 'legacy_action').callbacks).toEqual([
        { slot: 'handler', bindingId: null, readiness: 'unbound' },
      ]);
      expect(await om.getProperty(runtime, 'portable:1', 'computed_value')).toBe(2);
      await om.executeMutations(runtime, 'portable:1', [{
        mutation: 'portable_mutation',
      }]);
      await om.executeAction(runtime, 'portable:1', 'portable_action');
      await expect(om.validateConstraints(runtime, 'portable:1'))
        .rejects.toBeInstanceOf(om.BehaviorUnresolvedError);
      expect(calls).toEqual(['computed', 'mutation', 'action', 'interceptor']);

      om.registerConstraint(
        runtime,
        'PortableOwner',
        'conditional_rule',
        'id:when',
        () => true,
        'id:then',
        () => true
      );
      catalog = await om.getBehaviorCatalog(runtime);
      expect(entry(catalog, 'constraint', 'conditional_rule').callbacks).toEqual([
        { slot: 'when', bindingId: 'id:when', readiness: 'ready' },
        { slot: 'then', bindingId: 'id:then', readiness: 'ready' },
      ]);
      expect((await om.validateConstraints(runtime, 'portable:1')).valid).toBe(true);

      om.registerAction(runtime, 'PortableOwner', 'runtime_only', 'id:ghost', () => []);
      catalog = await om.getBehaviorCatalog(runtime);
      expect(entry(catalog, 'action', 'runtime_only')).toBeUndefined();
    } finally {
      db.close();
      om.clearRegistry(runtime);
    }
  });

  test('defines and executes custom validators returning string or null', async () => {
    const db = new CozoDb('mem', '', {});
    const runtime = om.createOmRuntime(db);
    await om.initSchema(runtime);

    try {
      await om.defineType(runtime, 'Resource', 'resource');
      await om.createEntity(runtime, 'resource:1', 'Resource', 'Resource 1');
      await om.defineConstraint(runtime, 'Resource', 'must_be_reviewed', {
        scope: 'custom',
        validator: () => 'Resource requires review',
      });
      await om.defineConstraint(runtime, 'Resource', 'must_have_owner', {
        scope: 'conditional',
        message: 'Resource requires an owner',
        when: () => true,
        then: () => true,
      });

      expect(await om.validateConstraints(runtime, 'resource:1')).toEqual({
        valid: false,
        errors: ['Resource requires review'],
      });
      expect((await om.validateEntity(runtime, 'resource:1')).errors).toContain(
        'Resource requires review'
      );

      om.registerValidator(runtime, 'Resource', 'must_be_reviewed', () => null);
      expect(await om.validateConstraints(runtime, 'resource:1')).toEqual({
        valid: true,
        errors: [],
      });

      om.clearRegistry(runtime);
      om.registerConstraint(
        runtime,
        'Resource',
        'must_have_owner',
        'binding:when',
        () => true,
        'binding:then',
        () => false
      );
      expect(await om.validateConstraints(runtime, 'resource:1', { types: ['conditional'] }))
        .toEqual({
          valid: false,
          errors: ["Constraint 'must_have_owner' violated: Resource requires an owner"],
        });
      await expect(
        om.defineConstraint(runtime, 'Resource', 'invalid_custom', {
          scope: 'custom',
          when: () => true,
          then: () => true,
          validator: () => null,
        })
      ).rejects.toThrow('cannot provide when(ctx) or then(ctx)');
    } finally {
      db.close();
      om.clearRegistry(runtime);
    }
  });

  test('allocates interceptor seq above persisted and runtime sparse entries across clear', async () => {
    const db = new CozoDb('mem', '', {});
    const runtime = om.createOmRuntime(db);
    await om.initSchema(runtime);

    try {
      await om.defineType(runtime, 'Resource', 'resource');
      for (const seq of [7, 11, 12, 21]) {
        await putInterceptorDefinition(db, 'Resource', 'deploy', 'before', seq, `seq ${seq}`);
      }
      om.registerInterceptor(
        runtime,
        'Resource',
        'deploy',
        'before',
        12,
        'binding:12',
        () => {}
      );

      await om.addInterceptor(runtime, 'Resource', 'deploy', 'before', () => {}, 'native 22');
      let rows = await db.run(
        '?[seq] := *om_interceptor_def{type_name: "Resource", action_name: "deploy", phase: "before", seq} :sort seq'
      );
      expect(rows.rows.map(([seq]) => seq)).toEqual([7, 11, 12, 21, 22]);

      om.clearRegistry(runtime);
      await om.addInterceptor(runtime, 'Resource', 'deploy', 'before', () => {}, 'native 23');
      rows = await db.run(
        '?[seq] := *om_interceptor_def{type_name: "Resource", action_name: "deploy", phase: "before", seq} :sort seq'
      );
      expect(rows.rows.map(([seq]) => seq)).toEqual([7, 11, 12, 21, 22, 23]);
    } finally {
      db.close();
      om.clearRegistry(runtime);
    }
  });

  test('derives readiness independently for two runtimes sharing one database', async () => {
    const db = new CozoDb('mem', '', {});
    const left = om.createOmRuntime(db);
    const right = om.createOmRuntime(db);
    await om.initSchema(left);

    try {
      await om.defineType(left, 'Resource', 'resource');
      await om.defineAction(left, 'Resource', 'inspect', () => []);
      await putBinding(db, {
        kind: 'action',
        owner: 'Resource',
        name: 'inspect',
        slot: 'handler',
        bindingId: 'binding:inspect',
      });
      om.registerAction(left, 'Resource', 'inspect', 'binding:inspect', () => []);
      om.registerAction(right, 'Resource', 'inspect', 'binding:other', () => []);

      expect(entry(await om.getBehaviorCatalog(left), 'action', 'inspect').callbacks[0].readiness)
        .toBe('ready');
      expect(entry(await om.getBehaviorCatalog(right), 'action', 'inspect').callbacks[0].readiness)
        .toBe('unresolved');

      om.registerAction(right, 'Resource', 'inspect', 'binding:inspect', () => []);
      om.clearRegistry(left);
      expect(entry(await om.getBehaviorCatalog(left), 'action', 'inspect').callbacks[0].readiness)
        .toBe('unresolved');
      expect(entry(await om.getBehaviorCatalog(right), 'action', 'inspect').callbacks[0].readiness)
        .toBe('ready');
    } finally {
      db.close();
      om.clearRegistry(left);
      om.clearRegistry(right);
    }
  });

  test('rejects invalid runtime registrations before publishing a snapshot', () => {
    const db = new CozoDb('mem', '', {});
    const runtime = om.createOmRuntime(db);
    try {
      expect(() => om.registerAction(db, 'Resource', 'inspect', 'id', () => []))
        .toThrow('expects an OM runtime');
      expect(() => om.registerComputed(runtime, '', 'score', 'id', () => 1))
        .toThrow('Type name is required');
      expect(() => om.registerMutation(runtime, 'Resource', 'save', '  ', () => {}))
        .toThrow('bindingId must be a non-empty string');
      expect(() =>
        om.registerInterceptor(runtime, 'Resource', 'deploy', 'during', 0, 'id', () => {})
      ).toThrow("phase must be 'before' or 'after'");
      expect(() =>
        om.registerInterceptor(runtime, 'Resource', 'deploy', 'before', -1, 'id', () => {})
      ).toThrow('seq must be a non-negative integer');
    } finally {
      db.close();
      om.clearRegistry(runtime);
    }
  });
});
