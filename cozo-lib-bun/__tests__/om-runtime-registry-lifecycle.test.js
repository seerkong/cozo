const { describe, expect, test } = require('bun:test');

const { CozoDb } = require('../index');
const om = require('../cozo-om');

async function createRuntimeDb() {
  const db = new CozoDb('mem', '', {});
  await om.initSchema(db);
  return { db, runtime: om.createOmRuntime(db) };
}

describe('OM runtime registry lifecycle', () => {
  test('same behavior key is isolated per runtime and clear targets one runtime', async () => {
    const db = new CozoDb('mem', '', {});
    await om.initSchema(db);
    const left = om.createOmRuntime(db);
    const right = om.createOmRuntime(db);
    const calls = [];

    try {
      expect(Object.keys(left)).toEqual(['runner']);
      expect(left.run).toBeUndefined();
      await om.defineType(left, 'Resource', 'Resource');
      await om.createEntity(left, 'resource:1', 'Resource', 'Resource');

      await om.defineAction(left, 'Resource', 'inspect', async () => {
        calls.push('left');
        return [];
      });
      await om.defineAction(right, 'Resource', 'inspect', async () => {
        calls.push('right');
        return [];
      });

      await om.executeAction(left, 'resource:1', 'inspect', {});
      await om.executeAction(right, 'resource:1', 'inspect', {});
      expect(calls).toEqual(['left', 'right']);

      om.clearRegistry(left);

      await expect(
        om.executeAction(left, 'resource:1', 'inspect', {})
      ).rejects.toThrow("Action 'inspect' not defined");
      const metadata = await db.run(
        '?[action_name] := *om_action_def{ type_name: "Resource", action_name }'
      );
      expect(metadata.rows).toEqual([['inspect']]);
      await om.executeAction(right, 'resource:1', 'inspect', {});
      expect(calls).toEqual(['left', 'right', 'right']);
    } finally {
      db.close();
    }
  });

  test('plain runners share only the legacy registry and no-arg clear leaves explicit runtimes intact', async () => {
    om.clearRegistry();
    const db = new CozoDb('mem', '', {});
    await om.initSchema(db);
    const explicit = om.createOmRuntime(db);
    const calls = [];

    try {
      await om.defineType(explicit, 'Resource', 'Resource');
      await om.createEntity(explicit, 'resource:1', 'Resource', 'Resource');

      await om.defineAction(explicit, 'Resource', 'inspect', async () => {
        calls.push('explicit');
        return [];
      });
      await om.defineAction(db, 'Resource', 'inspect', async () => {
        calls.push('legacy');
        return [];
      });

      await om.executeAction(explicit, 'resource:1', 'inspect', {});
      await om.executeAction(db, 'resource:1', 'inspect', {});
      expect(calls).toEqual(['explicit', 'legacy']);

      om.clearRegistry();

      await expect(
        om.executeAction(db, 'resource:1', 'inspect', {})
      ).rejects.toThrow("Action 'inspect' not defined");
      await om.executeAction(explicit, 'resource:1', 'inspect', {});
      expect(calls).toEqual(['explicit', 'legacy', 'explicit']);
    } finally {
      db.close();
      om.clearRegistry();
    }
  });

  test('plain runners intentionally share one legacy callback adapter across databases', async () => {
    om.clearRegistry();
    const leftDb = new CozoDb('mem', '', {});
    const rightDb = new CozoDb('mem', '', {});
    const calls = [];

    await om.initSchema(leftDb);
    await om.initSchema(rightDb);

    try {
      await om.defineType(leftDb, 'Resource', 'Resource');
      await om.defineType(rightDb, 'Resource', 'Resource');
      await om.createEntity(leftDb, 'left:1', 'Resource', 'Left resource');
      await om.createEntity(rightDb, 'right:1', 'Resource', 'Right resource');
      await om.defineAction(leftDb, 'Resource', 'inspect', async () => {
        calls.push('legacy');
        return [];
      });
      await rightDb.run(
        '?[type_name, action_name, description] <- [["Resource", "inspect", "right action"]]\n'
          + ':put om_action_def {type_name, action_name => description}'
      );

      await om.executeAction(rightDb, 'right:1', 'inspect', {});
      expect(calls).toEqual(['legacy']);

      om.clearRegistry();
      await expect(
        om.executeAction(leftDb, 'left:1', 'inspect', {})
      ).rejects.toThrow("Action 'inspect' not defined");
      await expect(
        om.executeAction(rightDb, 'right:1', 'inspect', {})
      ).rejects.toThrow("Action 'inspect' not defined");
    } finally {
      leftDb.close();
      rightDb.close();
      om.clearRegistry();
    }
  });

  test('transaction and indirect behavior paths retain the explicit runtime scope', async () => {
    om.clearRegistry();
    const { db, runtime } = await createRuntimeDb();
    const phases = [];

    try {
      await om.defineType(runtime, 'Resource', 'Resource');
      await om.defineType(runtime, 'ApiService', 'ApiService', { parentType: 'Resource' });
      await om.defineAttribute(runtime, 'Resource', 'status', 'String', false);
      await om.defineAttribute(runtime, 'Resource', 'base', 'Number', false);
      await om.defineAttribute(runtime, 'Resource', 'note', 'String', false);
      await om.defineRelation(runtime, 'depends_on', 'Resource', 'Resource');
      await om.createEntity(runtime, 'apiService:1', 'ApiService', 'ApiService 1');
      await om.createEntity(runtime, 'apiService:2', 'ApiService', 'ApiService 2');
      await om.createEntity(runtime, 'apiService:3', 'ApiService', 'ApiService 3');

      await om.defineComputed(runtime, 'Resource', 'bonus', async () => 1);
      await om.defineComputed(db, 'Resource', 'bonus', async () => 1000);
      await om.defineComputed(runtime, 'Resource', 'score', async (ctx) => {
        phases.push('computed');
        const base = await om.getProperty(ctx.runtime, ctx.entityId, 'base');
        const bonus = await om.getProperty(ctx.runtime, ctx.entityId, 'bonus');
        return Number(base) * 2 + Number(bonus);
      });
      await om.defineComputed(runtime, 'Resource', 'temporal_score', async (ctx) => {
        const base = await om.getPropertyAsOf(ctx.runtime, ctx.entityId, 'base', ctx.asOf);
        return Number(base) * 3;
      });
      await om.defineConstraint(runtime, 'Resource', 'score_limit', {
        scope: 'conditional',
        when: async () => true,
        then: async (ctx) =>
          Number(await om.getProperty(ctx.runtime, ctx.entityId, 'score')) <= 10,
      });
      await om.defineConstraint(runtime, 'Resource', 'one_dependency', {
        scope: 'cross-entity',
        when: async () => true,
        then: async (ctx) => {
          const neighbors = await om.getNeighbors(
            ctx.runtime,
            ctx.entityId,
            'depends_on',
            'outgoing'
          );
          return neighbors.outgoing.length <= 1;
        },
      });

      await om.setProperty(runtime, 'apiService:1', 'base', 4);
      expect(await om.getProperty(runtime, 'apiService:1', 'score')).toBe(9);
      expect((await om.getEntityView(runtime, 'apiService:1')).properties.score).toBe(9);
      expect(
        await om.getPropertyAsOf(runtime, 'apiService:1', 'temporal_score', '2100-01-01T00:00:00Z')
      ).toBe(12);
      await expect(
        om.setProperty(runtime, 'apiService:1', 'base', 5)
      ).rejects.toThrow("Constraint 'score_limit' violated");
      expect(await om.getProperty(runtime, 'apiService:1', 'base')).toBe(4);

      await om.linkEntities(runtime, 'apiService:1', 'depends_on', 'apiService:2');
      await expect(
        om.linkEntities(runtime, 'apiService:1', 'depends_on', 'apiService:3')
      ).rejects.toThrow("Constraint 'one_dependency' violated");

      await om.defineMutation(runtime, 'Resource', 'setStatus', async (ctx, params) => {
        phases.push('mutation');
        await om.setProperty(ctx.runtime, ctx.entityId, 'status', String(params.status));
      });
      await om.defineAction(runtime, 'Resource', 'activate', async (ctx) => {
        phases.push(`parent:${await om.getProperty(ctx.runtime, ctx.entityId, 'score')}`);
        return [{ mutation: 'setStatus', params: { status: 'active' } }];
      });
      await om.defineAction(runtime, 'ApiService', 'activate', async (ctx, params) => {
        phases.push(`child:${await om.getProperty(ctx.runtime, ctx.entityId, 'score')}`);
        return ctx.callParentAction('activate', params);
      });
      await om.addInterceptor(runtime, 'Resource', 'activate', 'before', async (ctx) => {
        phases.push(`before:${await om.getProperty(ctx.runtime, ctx.entityId, 'score')}`);
      });
      await om.addInterceptor(runtime, 'ApiService', 'activate', 'after', async (ctx) => {
        phases.push(`after:${await om.getProperty(ctx.runtime, ctx.entityId, 'status')}`);
      });

      await om.executeAction(runtime, 'apiService:1', 'activate', {});
      expect(await om.getProperty(runtime, 'apiService:1', 'status')).toBe('active');
      expect(phases).toContain('before:9');
      expect(phases).toContain('child:9');
      expect(phases).toContain('parent:9');
      expect(phases).toContain('mutation');
      expect(phases).toContain('after:active');

      om.clearRegistry(runtime);
      expect(await om.getProperty(runtime, 'apiService:1', 'score')).toBeUndefined();
      await om.setProperty(runtime, 'apiService:1', 'base', 100);
      await om.linkEntities(runtime, 'apiService:1', 'depends_on', 'apiService:3');
      expect(await om.getProperty(runtime, 'apiService:1', 'base')).toBe(100);
    } finally {
      db.close();
      om.clearRegistry();
    }
  });

  test('an in-flight action completes on one registry snapshot while later commands see clear', async () => {
    const { db, runtime } = await createRuntimeDb();
    let releaseAction;
    let actionStarted;
    const started = new Promise((resolve) => {
      actionStarted = resolve;
    });
    const release = new Promise((resolve) => {
      releaseAction = resolve;
    });
    let observedMarker;

    try {
      await om.defineType(runtime, 'Resource', 'Resource');
      await om.defineType(runtime, 'ApiService', 'ApiService', { parentType: 'Resource' });
      await om.defineAttribute(runtime, 'Resource', 'status', 'String', false);
      await om.defineAttribute(runtime, 'Resource', 'note', 'String', false);
      await om.createEntity(runtime, 'apiService:1', 'ApiService', 'ApiService');

      await om.defineComputed(runtime, 'Resource', 'marker', async () => 'old-snapshot');
      await om.defineMutation(runtime, 'Resource', 'setStatus', async (ctx) => {
        await om.setProperty(ctx.runtime, ctx.entityId, 'status', 'completed');
      });
      await om.defineAction(runtime, 'Resource', 'deploy', async () => [
        { mutation: 'setStatus' },
      ]);
      await om.defineAction(runtime, 'ApiService', 'deploy', async (ctx, params) => {
        actionStarted();
        await release;
        observedMarker = await om.getProperty(ctx.runtime, ctx.entityId, 'marker');
        return ctx.callParentAction('deploy', params);
      });
      await om.addInterceptor(runtime, 'ApiService', 'deploy', 'after', async (ctx) => {
        await om.setProperty(ctx.runtime, ctx.entityId, 'note', 'after-ran');
      });

      const inFlight = om.executeAction(runtime, 'apiService:1', 'deploy', {});
      await actionStarted;
      om.clearRegistry(runtime);
      releaseAction();
      await inFlight;

      expect(observedMarker).toBe('old-snapshot');
      expect(await om.getProperty(runtime, 'apiService:1', 'status')).toBe('completed');
      expect(await om.getProperty(runtime, 'apiService:1', 'note')).toBe('after-ran');
      await expect(
        om.executeAction(runtime, 'apiService:1', 'deploy', {})
      ).rejects.toThrow("Action 'deploy' not defined");
    } finally {
      releaseAction();
      db.close();
    }
  });
});
