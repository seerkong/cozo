const { expect, test, describe } = require('bun:test');

const { createTestDb } = require('./helpers');
const dsl = require('../cozo-dsl');

describe('Phase 2 (track add-action-and-constraints): schema extensions', () => {
  test('initSchema creates action/mutation/interceptor/constraint/computed relations', async () => {
    const { db } = await createTestDb();
    try {
      const qAction = dsl.query()
        .select(['type_name'])
        .fromStored('om_action_def', {
          type_name: dsl.var('type_name'),
          action_name: dsl.var('_a'),
          description: dsl.var('_d'),
        })
        .limit(1)
        .build();
      await expect(db.run(qAction.script, qAction.params)).resolves.toBeTruthy();

      const qMutation = dsl.query()
        .select(['type_name'])
        .fromStored('om_mutation_def', {
          type_name: dsl.var('type_name'),
          mutation_name: dsl.var('_m'),
          description: dsl.var('_d'),
        })
        .limit(1)
        .build();
      await expect(db.run(qMutation.script, qMutation.params)).resolves.toBeTruthy();

      const qInterceptor = dsl.query()
        .select(['type_name'])
        .fromStored('om_interceptor_def', {
          type_name: dsl.var('type_name'),
          action_name: dsl.var('_a'),
          phase: dsl.var('_p'),
          seq: dsl.var('_s'),
          description: dsl.var('_d'),
        })
        .limit(1)
        .build();
      await expect(db.run(qInterceptor.script, qInterceptor.params)).resolves.toBeTruthy();

      const qConstraint = dsl.query()
        .select(['type_name'])
        .fromStored('om_constraint_def', {
          type_name: dsl.var('type_name'),
          constraint_name: dsl.var('_c'),
          constraint_type: dsl.var('_t'),
          message: dsl.var('_m'),
        })
        .limit(1)
        .build();
      await expect(db.run(qConstraint.script, qConstraint.params)).resolves.toBeTruthy();

      const qComputed = dsl.query()
        .select(['type_name'])
        .fromStored('om_computed_def', {
          type_name: dsl.var('type_name'),
          attr_name: dsl.var('_a'),
          description: dsl.var('_d'),
        })
        .limit(1)
        .build();
      await expect(db.run(qComputed.script, qComputed.params)).resolves.toBeTruthy();
    } finally {
      db.close();
    }
  });

  test('backward compat: existing OM APIs still work after initSchema', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Resource', 'Resource');
      await om.defineAttribute(db, 'Resource', 'name', 'String', true);

      await om.createEntity(db, 'resource:1', 'Resource', 'Resource #1');
      await om.setProperty(db, 'resource:1', 'name', 'Printer');
      await om.finalizeEntity(db, 'resource:1');

      const view = await om.getEntityView(db, 'resource:1');
      expect(view).toBeTruthy();
      expect(view.id).toBe('resource:1');
      expect(view.typeName).toBe('Resource');
      expect(view.properties.name).toBe('Printer');
    } finally {
      db.close();
    }
  });

  test('clearRegistry exists and is callable', async () => {
    const { db, om } = await createTestDb();
    try {
      expect(typeof om.clearRegistry).toBe('function');
      expect(() => om.clearRegistry()).not.toThrow();

      // Clearing registries should not affect core schema/data APIs.
      await om.defineType(db, 'Resource', 'Resource');
      await om.createEntity(db, 'resource:2', 'Resource', 'Resource #2');
      const view = await om.getEntityView(db, 'resource:2');
      expect(view.id).toBe('resource:2');
    } finally {
      db.close();
    }
  });

  test('behavior definitions reject invalid owners, scopes, and phases before effects', async () => {
    const { db, om } = await createTestDb();
    try {
      await expect(
        om.defineAction(db, 'MissingOwner', 'guarded_action', async () => [])
      ).rejects.toThrow(/does not exist/i);

      await om.defineType(db, 'GuardOwner', 'Guard owner');
      await om.createEntity(db, 'guard:owner', 'GuardOwner', 'Guard owner');

      await expect(
        om.defineConstraint(db, 'GuardOwner', 'invalid_scope', {
          scope: 'request',
          when: async () => true,
          then: async () => true,
        })
      ).rejects.toThrow(/Unsupported constraint scope/i);

      const interceptorEvents = [];
      await expect(
        om.addInterceptor(db, 'GuardOwner', 'guarded_action', 'around', async () => {
          interceptorEvents.push('invalid');
        })
      ).rejects.toThrow(/before.*after/i);

      for (const [relation, keys, projection, values] of [
        [
          'om_action_def',
          { type_name: 'MissingOwner', action_name: 'guarded_action' },
          'description',
          { description: dsl.var('description') },
        ],
        [
          'om_constraint_def',
          { type_name: 'GuardOwner', constraint_name: 'invalid_scope' },
          'constraint_type',
          { constraint_type: dsl.var('constraint_type'), message: dsl.var('_message') },
        ],
        [
          'om_interceptor_def',
          { type_name: 'GuardOwner', action_name: 'guarded_action' },
          'phase',
          {
            phase: dsl.var('phase'),
            seq: dsl.var('_seq'),
            description: dsl.var('_description'),
          },
        ],
      ]) {
        const keyVars = Object.fromEntries(
          Object.keys(keys).map((name) => [name, dsl.param(name, keys[name])])
        );
        const queryResult = dsl.query()
          .select([projection])
          .fromStored(relation, {
            ...keyVars,
            ...values,
          })
          .build();
        expect((await db.run(queryResult.script, queryResult.params)).rows).toHaveLength(0);
      }

      await om.defineAction(db, 'GuardOwner', 'guarded_action', async () => []);
      await om.addInterceptor(db, 'GuardOwner', 'guarded_action', 'before', async () => {
        interceptorEvents.push('valid');
      });
      await om.executeAction(db, 'guard:owner', 'guarded_action', {});
      expect(interceptorEvents).toEqual(['valid']);
    } finally {
      db.close();
    }
  });
});
