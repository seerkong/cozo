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
      await om.defineType(db, 'Asset', 'Asset');
      await om.defineAttribute(db, 'Asset', 'name', 'String', true);

      await om.createEntity(db, 'asset:1', 'Asset', 'Asset #1');
      await om.setProperty(db, 'asset:1', 'name', 'Printer');
      await om.finalizeEntity(db, 'asset:1');

      const view = await om.getEntityView(db, 'asset:1');
      expect(view).toBeTruthy();
      expect(view.id).toBe('asset:1');
      expect(view.typeName).toBe('Asset');
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
      await om.defineType(db, 'Asset', 'Asset');
      await om.createEntity(db, 'asset:2', 'Asset', 'Asset #2');
      const view = await om.getEntityView(db, 'asset:2');
      expect(view.id).toBe('asset:2');
    } finally {
      db.close();
    }
  });
});
