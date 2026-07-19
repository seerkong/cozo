const { describe, expect, test } = require('bun:test');

const { CozoDb } = require('../index');
const om = require('../cozo-om');

function behavior(kind, ownerType, name, slot, bindingId, metadata = {}) {
  return {
    kind,
    ownerType,
    name,
    constraintType: metadata.constraintType ?? null,
    message: metadata.message ?? null,
    description: metadata.description ?? null,
    interceptorPhase: metadata.interceptorPhase ?? null,
    interceptorSeq: metadata.interceptorSeq ?? null,
    callbacks: [{ slot, bindingId, readiness: metadata.readiness || 'ready' }],
  };
}

function manifest(behaviors) {
  return new TextDecoder().decode(om.encodeBehaviorManifestJson({ behaviors }));
}

function entry(catalog, kind, name, phase = null, seq = null) {
  return catalog.behaviors.find((candidate) =>
    candidate.kind === kind
    && candidate.name === name
    && candidate.interceptorPhase === phase
    && candidate.interceptorSeq === seq
  );
}

function callbacks(overrides = {}) {
  return {
    constraints: [
      { bindingId: 'id:when', callback: () => true },
      { bindingId: 'id:then', callback: () => true },
    ],
    validators: [{ bindingId: 'id:validator', callback: () => null }],
    computed: [{ bindingId: 'id:computed', callback: () => 42 }],
    actions: [{ bindingId: 'id:action', callback: () => [] }],
    mutations: [{ bindingId: 'id:mutation', callback: () => {} }],
    interceptors: [{ bindingId: 'id:interceptor', callback: () => {} }],
    ...overrides,
  };
}

function portableCatalog(owner = 'ImportOwner') {
  return [
    {
      kind: 'constraint',
      ownerType: owner,
      name: 'conditional_rule',
      constraintType: 'conditional',
      message: 'conditional message',
      description: null,
      interceptorPhase: null,
      interceptorSeq: null,
      callbacks: [
        { slot: 'when', bindingId: 'id:when', readiness: 'ready' },
        { slot: 'then', bindingId: 'id:then', readiness: 'ready' },
      ],
    },
    behavior('constraint', owner, 'custom_rule', 'validator', 'id:validator', {
      constraintType: 'custom',
      message: 'custom message',
    }),
    behavior('computed', owner, 'score', 'compute', 'id:computed', {
      description: 'computed score',
    }),
    behavior('action', owner, 'inspect', 'handler', 'id:action', {
      description: 'inspect action',
    }),
    behavior('mutation', owner, 'save', 'executor', 'id:mutation', {
      description: 'save mutation',
    }),
    behavior('interceptor', owner, 'inspect', 'handler', 'id:interceptor', {
      description: 'before inspect',
      interceptorPhase: 'before',
      interceptorSeq: 7,
    }),
  ];
}

function wrappingDb(db, hooks = {}) {
  let transactionNumber = 0;
  return {
    run(script, params) {
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

async function captureError(promise) {
  try {
    await promise;
  } catch (error) {
    return error;
  }
  throw new Error('Expected operation to reject');
}

describe('OM behavior portability atomic import', () => {
  test('permissive and strict import derive readiness and cover all behavior kinds', async () => {
    const db = new CozoDb('mem', '', {});
    const runtime = om.createOmRuntime(db);
    await om.initSchema(runtime);

    try {
      await om.defineType(runtime, 'ImportOwner', 'import owner');
      await om.createEntity(runtime, 'import:1', 'ImportOwner', 'Import 1');
      const json = manifest(portableCatalog());
      const partial = callbacks({
        constraints: [{ bindingId: 'id:when', callback: () => true }],
      });

      const permissive = await om.importBehaviorManifestJson(runtime, json, partial);
      expect(permissive.applied).toBe(true);
      expect(permissive.unresolved).toEqual([
        expect.objectContaining({
          code: 'OMR1001',
          slot: 'then',
          bindingId: 'id:then',
        }),
      ]);
      expect(permissive.diagnostics.map((diagnostic) => diagnostic.code)).toContain(
        'OMI1201'
      );

      const imported = await om.getBehaviorCatalog(runtime);
      expect(imported.behaviors).toHaveLength(6);
      expect(new Set(imported.behaviors.map((candidate) => candidate.kind))).toEqual(
        new Set(['constraint', 'computed', 'action', 'mutation', 'interceptor'])
      );
      expect(entry(imported, 'constraint', 'conditional_rule').callbacks).toEqual([
        { slot: 'when', bindingId: 'id:when', readiness: 'ready' },
        { slot: 'then', bindingId: 'id:then', readiness: 'unresolved' },
      ]);
      await expect(om.validateEntity(runtime, 'import:1'))
        .rejects.toBeInstanceOf(om.BehaviorUnresolvedError);

      const beforeStrict = await om.exportBehaviorManifestJson(runtime);
      const strict = await om.importBehaviorManifestJson(
        runtime,
        json,
        {},
        { requireReady: true }
      );
      expect(strict.applied).toBe(false);
      expect(strict.unresolved).toHaveLength(7);
      expect(await om.exportBehaviorManifestJson(runtime)).toEqual(beforeStrict);

      const ready = await om.importBehaviorManifestJson(
        runtime,
        json,
        callbacks(),
        { requireReady: true }
      );
      expect(ready).toEqual({
        applied: true,
        diagnostics: [],
        unresolved: [],
      });
      expect(
        (await om.getBehaviorCatalog(runtime)).behaviors
          .flatMap((candidate) => candidate.callbacks)
          .every((callback) => callback.readiness === 'ready')
      ).toBe(true);
      expect((await om.validateEntity(runtime, 'import:1')).valid).toBe(true);
      expect(await om.getProperty(runtime, 'import:1', 'score')).toBe(42);
      await om.executeMutations(runtime, 'import:1', [{ mutation: 'save' }]);
      await om.executeAction(runtime, 'import:1', 'inspect');

      om.clearRegistry(runtime);
      expect(
        (await om.getBehaviorCatalog(runtime)).behaviors
          .flatMap((candidate) => candidate.callbacks)
          .every((callback) => callback.readiness === 'unresolved')
      ).toBe(true);
      expect((await om.importBehaviorManifestJson(
        runtime,
        json,
        callbacks(),
        { requireReady: true }
      )).applied).toBe(true);
      expect(
        (await om.getBehaviorCatalog(runtime)).behaviors
          .flatMap((candidate) => candidate.callbacks)
          .every((callback) => callback.readiness === 'ready')
      ).toBe(true);
    } finally {
      db.close();
      om.clearRegistry(runtime);
    }
  });

  test('preflight rejects callback index, owner, constraint shape, and unbound strict input', async () => {
    const db = new CozoDb('mem', '', {});
    const runtime = om.createOmRuntime(db);
    await om.initSchema(runtime);

    try {
      await om.defineType(runtime, 'ImportOwner', 'import owner');
      const baseline = await om.exportBehaviorManifestJson(runtime);
      const actionJson = manifest([
        behavior('action', 'ImportOwner', 'inspect', 'handler', 'id:action'),
      ]);

      const duplicate = await om.importBehaviorManifestJson(runtime, actionJson, {
        actions: [
          { bindingId: 'id:action', callback: () => [] },
          { bindingId: 'id:action', callback: () => [] },
        ],
      });
      expect(duplicate).toMatchObject({
        applied: false,
        diagnostics: [expect.objectContaining({ code: 'OMI1002', path: '$callbacks' })],
      });

      const invalidCallback = await om.importBehaviorManifestJson(runtime, actionJson, {
        actions: [{ bindingId: '', callback: null }],
      });
      expect(invalidCallback.diagnostics[0].code).toBe('OMI1001');

      const missingOwner = await om.importBehaviorManifestJson(
        runtime,
        manifest([behavior('action', 'MissingOwner', 'inspect', 'handler', 'id:action')]),
        callbacks()
      );
      expect(missingOwner.diagnostics[0]).toMatchObject({
        code: 'OMI1101',
        ownerType: 'MissingOwner',
      });

      const invalidShape = await om.importBehaviorManifestJson(runtime, manifest([{
        kind: 'constraint',
        ownerType: 'ImportOwner',
        name: 'bad_shape',
        constraintType: 'conditional',
        message: '',
        description: null,
        interceptorPhase: null,
        interceptorSeq: null,
        callbacks: [{ slot: 'when', bindingId: 'id:when', readiness: 'ready' }],
      }]), callbacks());
      expect(invalidShape.diagnostics).toEqual([
        expect.objectContaining({
          code: 'OMI1104',
          path: 'constraint:ImportOwner/bad_shape.callbacks',
        }),
      ]);

      const incompatible = await om.importBehaviorManifestJson(runtime, actionJson, {
        computed: [{ bindingId: 'id:action', callback: () => 1 }],
      });
      expect(incompatible).toMatchObject({
        applied: true,
        diagnostics: [expect.objectContaining({ code: 'OMI1202' })],
        unresolved: [expect.objectContaining({ bindingId: 'id:action' })],
      });

      const unboundJson = manifest([
        behavior('action', 'ImportOwner', 'native', 'handler', null, {
          readiness: 'unbound',
        }),
      ]);
      const unboundStrict = await om.importBehaviorManifestJson(
        runtime,
        unboundJson,
        {},
        { requireReady: true }
      );
      expect(unboundStrict).toMatchObject({
        applied: false,
        diagnostics: [expect.objectContaining({ code: 'OMI2001' })],
      });
      expect(await om.exportBehaviorManifestJson(runtime)).not.toEqual(baseline);
      expect(entry(await om.getBehaviorCatalog(runtime), 'action', 'native')).toBeUndefined();
    } finally {
      db.close();
      om.clearRegistry(runtime);
    }
  });

  test('persistent failure leaves state unchanged and CAS failure compensates', async () => {
    const db = new CozoDb('mem', '', {});
    let runtime;
    let casConflict = false;
    let concurrentCalls = 0;
    const wrapped = wrappingDb(db, {
      beforeCommit(transactionNumber) {
        if (transactionNumber === 1 && !casConflict) {
          throw new Error('simulated persistence failure');
        }
      },
      afterCommit(transactionNumber) {
        if (transactionNumber === 2) {
          casConflict = true;
          om.registerAction(
            runtime,
            'ImportOwner',
            'inspect',
            'id:concurrent',
            () => {
              concurrentCalls++;
              return [];
            }
          );
        }
      },
    });
    runtime = om.createOmRuntime(wrapped);
    await om.initSchema(runtime);

    try {
      await om.defineType(runtime, 'ImportOwner', 'import owner');
      await om.defineAction(runtime, 'ImportOwner', 'inspect', () => [], 'old');
      const baseline = await om.exportBehaviorManifestJson(runtime);
      const json = manifest([
        behavior('action', 'ImportOwner', 'inspect', 'handler', 'id:action', {
          description: 'changed',
        }),
      ]);

      const persistentError = await captureError(
        om.importBehaviorManifestJson(runtime, json, callbacks())
      );
      expect(persistentError).toBeInstanceOf(om.BehaviorImportError);
      expect(persistentError.message).toBe(
        'Behavior manifest persistence failed before registry publication.'
      );
      expect(persistentError.originalFailure.message).toBe(
        'simulated persistence failure'
      );
      expect(persistentError.compensationFailures).toEqual([]);
      expect(await om.exportBehaviorManifestJson(runtime)).toEqual(baseline);

      const publishError = await captureError(
        om.importBehaviorManifestJson(runtime, json, callbacks())
      );
      expect(publishError).toBeInstanceOf(om.BehaviorImportError);
      expect(publishError.message).toBe(
        'Behavior registry publication failed; persistent state was restored.'
      );
      expect(publishError.originalFailure.name).toBe(
        'BehaviorRegistryPublicationConflictError'
      );
      expect(publishError.compensationFailures).toEqual([]);
      const restored = await om.getBehaviorCatalog(runtime);
      expect(entry(restored, 'action', 'inspect')).toMatchObject({
        description: 'old',
        callbacks: [{
          bindingId: null,
          readiness: 'unbound',
        }],
      });
      await om.createEntity(runtime, 'import:cas', 'ImportOwner', 'CAS');
      await om.executeAction(runtime, 'import:cas', 'inspect');
      expect(concurrentCalls).toBe(1);
    } finally {
      db.close();
      om.clearRegistry(runtime);
    }
  });

  test('reports incomplete compensation without replacing the concurrent registry writer', async () => {
    const db = new CozoDb('mem', '', {});
    let runtime;
    const wrapped = wrappingDb(db, {
      beforeRun(transactionNumber) {
        if (transactionNumber === 2) {
          throw new Error('simulated compensation failure');
        }
      },
      afterCommit(transactionNumber) {
        if (transactionNumber === 1) {
          om.registerAction(
            runtime,
            'ImportOwner',
            'inspect',
            'id:concurrent',
            () => []
          );
        }
      },
    });
    runtime = om.createOmRuntime(wrapped);
    await om.initSchema(runtime);

    try {
      await om.defineType(runtime, 'ImportOwner', 'import owner');
      const json = manifest([
        behavior('action', 'ImportOwner', 'inspect', 'handler', 'id:action'),
      ]);
      const error = await captureError(
        om.importBehaviorManifestJson(runtime, json, callbacks())
      );
      expect(error).toBeInstanceOf(om.BehaviorImportError);
      expect(error.message).toBe(
        'Behavior registry publication failed and persistent compensation was incomplete.'
      );
      expect(error.compensationFailures).toHaveLength(1);
      expect(error.compensationFailures[0].message).toBe(
        'simulated compensation failure'
      );
      expect(entry(await om.getBehaviorCatalog(runtime), 'action', 'inspect').callbacks[0])
        .toEqual({
          slot: 'handler',
          bindingId: 'id:action',
          readiness: 'unresolved',
        });
    } finally {
      db.close();
      om.clearRegistry(runtime);
    }
  });

  test('gates catalog readers, releases before callbacks, and isolates shared-db runtimes', async () => {
    const db = new CozoDb('mem', '', {});
    let releaseWrite;
    let writeEntered;
    const entered = new Promise((resolve) => {
      writeEntered = resolve;
    });
    const release = new Promise((resolve) => {
      releaseWrite = resolve;
    });
    let delayed = false;
    const wrapped = wrappingDb(db, {
      async beforeRun(transactionNumber) {
        if (transactionNumber === 1 && !delayed) {
          delayed = true;
          writeEntered();
          await release;
        }
      },
    });
    const left = om.createOmRuntime(wrapped);
    const right = om.createOmRuntime(wrapped);
    await om.initSchema(left);

    try {
      await om.defineType(left, 'ImportOwner', 'import owner');
      await om.createEntity(left, 'import:1', 'ImportOwner', 'Import 1');
      const json = manifest([
        behavior('action', 'ImportOwner', 'inspect', 'handler', 'id:action'),
      ]);
      let reentrantCatalog;
      const importPromise = om.importBehaviorManifestJson(left, json, {
        actions: [{
          bindingId: 'id:action',
          callback: async (ctx) => {
            reentrantCatalog = await om.getBehaviorCatalog(ctx.runtime);
            return [];
          },
        }],
      }, { requireReady: true });

      await entered;
      let readerResolved = false;
      const reader = om.getBehaviorCatalog(left).then((catalog) => {
        readerResolved = true;
        return catalog;
      });
      await Bun.sleep(10);
      expect(readerResolved).toBe(false);
      releaseWrite();

      expect((await importPromise).applied).toBe(true);
      expect(entry(await reader, 'action', 'inspect').callbacks[0].readiness).toBe('ready');
      expect(entry(await om.getBehaviorCatalog(right), 'action', 'inspect').callbacks[0])
        .toEqual({
          slot: 'handler',
          bindingId: 'id:action',
          readiness: 'unresolved',
        });

      await om.executeAction(left, 'import:1', 'inspect');
      expect(entry(reentrantCatalog, 'action', 'inspect').callbacks[0].readiness).toBe(
        'ready'
      );
    } finally {
      db.close();
      om.clearRegistry(left);
      om.clearRegistry(right);
    }
  });

  test('queues explicit runtime clear behind import publication', async () => {
    const db = new CozoDb('mem', '', {});
    let releaseWrite;
    let writeEntered;
    const entered = new Promise((resolve) => {
      writeEntered = resolve;
    });
    const release = new Promise((resolve) => {
      releaseWrite = resolve;
    });
    let delayed = false;
    const wrapped = wrappingDb(db, {
      async beforeRun(transactionNumber) {
        if (transactionNumber === 1 && !delayed) {
          delayed = true;
          writeEntered();
          await release;
        }
      },
    });
    const runtime = om.createOmRuntime(wrapped);
    await om.initSchema(runtime);

    try {
      await om.defineType(runtime, 'ImportOwner', 'import owner');
      await om.defineAction(runtime, 'ImportOwner', 'inspect', () => [], 'old');
      const json = manifest([
        behavior('action', 'ImportOwner', 'inspect', 'handler', 'id:action', {
          description: 'changed',
        }),
      ]);
      const importPromise = om.importBehaviorManifestJson(
        runtime,
        json,
        callbacks(),
        { requireReady: true }
      );

      await entered;
      let clearResolved = false;
      const clearPromise = om.clearRegistry(runtime);
      expect(clearPromise).toBeInstanceOf(Promise);
      clearPromise.then(() => {
        clearResolved = true;
      });
      await Bun.sleep(10);
      expect(clearResolved).toBe(false);

      releaseWrite();
      expect((await importPromise).applied).toBe(true);
      await clearPromise;

      const imported = entry(
        await om.getBehaviorCatalog(runtime),
        'action',
        'inspect'
      );
      expect(imported).toMatchObject({
        description: 'changed',
        callbacks: [{
          slot: 'handler',
          bindingId: 'id:action',
          readiness: 'unresolved',
        }],
      });
    } finally {
      db.close();
      await om.clearRegistry(runtime);
    }
  });
});
