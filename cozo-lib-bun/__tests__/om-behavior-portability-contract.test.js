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
  [[$behavior_kind, $owner_type, $behavior_name, $callback_slot, $phase, $seq, $binding_id]]
:put om_behavior_binding {
  behavior_kind, owner_type, behavior_name, callback_slot, phase, seq => binding_id
}
    `.trim(),
    {
      behavior_kind: kind,
      owner_type: owner,
      behavior_name: name,
      callback_slot: slot,
      phase,
      seq,
      binding_id: bindingId,
    }
  );
}

function catalogEntry(catalog, kind, name, phase = null, seq = null) {
  return catalog.behaviors.find((entry) =>
    entry.kind === kind
    && entry.name === name
    && entry.interceptorPhase === phase
    && entry.interceptorSeq === seq
  );
}

describe('OM behavior portability V1 contract', () => {
  test('initSchema and catalog project five kinds, six slots, and persisted binding readiness', async () => {
    const db = new CozoDb('mem', '', {});
    const runtime = om.createOmRuntime(db);
    await om.initSchema(runtime);

    try {
      const bindingRelation = await db.run(
        '?[behavior_kind] := *om_behavior_binding{behavior_kind}'
      );
      expect(bindingRelation.rows).toEqual([]);

      await om.defineType(runtime, 'PortableOwner', 'portable owner');
      await om.defineConstraint(runtime, 'PortableOwner', 'conditional_rule', {
        scope: 'conditional',
        message: 'conditional message',
        when: () => true,
        then: () => true,
      });
      await db.run(
        `
?[type_name, constraint_name, constraint_type, message] <-
  [["PortableOwner", "custom_rule", "custom", "custom message"]]
:put om_constraint_def {type_name, constraint_name => constraint_type, message}
        `.trim()
      );
      await om.defineComputed(
        runtime,
        'PortableOwner',
        'computed_value',
        () => 1,
        'computed description'
      );
      await om.defineAction(
        runtime,
        'PortableOwner',
        'portable_action',
        () => [],
        'action description'
      );
      await om.defineAction(
        runtime,
        'PortableOwner',
        'native_action',
        () => [],
        'native description'
      );
      await om.defineMutation(
        runtime,
        'PortableOwner',
        'portable_mutation',
        () => {},
        'mutation description'
      );
      await om.addInterceptor(
        runtime,
        'PortableOwner',
        'portable_action',
        'after',
        () => {},
        'interceptor description'
      );

      const bindings = [
        {
          kind: 'constraint',
          owner: 'PortableOwner',
          name: 'conditional_rule',
          slot: 'when',
          bindingId: 'binding:constraint:when',
        },
        {
          kind: 'constraint',
          owner: 'PortableOwner',
          name: 'conditional_rule',
          slot: 'then',
          bindingId: 'binding:constraint:then',
        },
        {
          kind: 'constraint',
          owner: 'PortableOwner',
          name: 'custom_rule',
          slot: 'validator',
          bindingId: 'binding:constraint:validator',
        },
        {
          kind: 'computed',
          owner: 'PortableOwner',
          name: 'computed_value',
          slot: 'compute',
          bindingId: 'binding:computed',
        },
        {
          kind: 'action',
          owner: 'PortableOwner',
          name: 'portable_action',
          slot: 'handler',
          bindingId: 'binding:action',
        },
        {
          kind: 'mutation',
          owner: 'PortableOwner',
          name: 'portable_mutation',
          slot: 'executor',
          bindingId: 'binding:mutation',
        },
        {
          kind: 'interceptor',
          owner: 'PortableOwner',
          name: 'portable_action',
          slot: 'handler',
          phase: 'after',
          seq: 0,
          bindingId: 'binding:interceptor',
        },
      ];
      for (const binding of bindings) {
        await putBinding(db, binding);
      }

      const catalog = await om.getBehaviorCatalog(runtime);
      expect([...new Set(catalog.behaviors.map((entry) => entry.kind))]).toEqual([
        'constraint',
        'computed',
        'action',
        'mutation',
        'interceptor',
      ]);
      expect(catalogEntry(catalog, 'constraint', 'conditional_rule').callbacks).toEqual([
        {
          slot: 'when',
          bindingId: 'binding:constraint:when',
          readiness: 'unresolved',
        },
        {
          slot: 'then',
          bindingId: 'binding:constraint:then',
          readiness: 'unresolved',
        },
      ]);
      expect(catalogEntry(catalog, 'constraint', 'custom_rule')).toMatchObject({
        constraintType: 'custom',
        message: 'custom message',
        description: null,
        callbacks: [{
          slot: 'validator',
          bindingId: 'binding:constraint:validator',
          readiness: 'unresolved',
        }],
      });
      expect(catalogEntry(catalog, 'computed', 'computed_value').callbacks[0]).toEqual({
        slot: 'compute',
        bindingId: 'binding:computed',
        readiness: 'unresolved',
      });
      expect(catalogEntry(catalog, 'action', 'portable_action').callbacks[0]).toEqual({
        slot: 'handler',
        bindingId: 'binding:action',
        readiness: 'unresolved',
      });
      expect(catalogEntry(catalog, 'mutation', 'portable_mutation').callbacks[0]).toEqual({
        slot: 'executor',
        bindingId: 'binding:mutation',
        readiness: 'unresolved',
      });
      expect(catalogEntry(catalog, 'interceptor', 'portable_action', 'after', 0)).toMatchObject({
        description: 'interceptor description',
        callbacks: [{
          slot: 'handler',
          bindingId: 'binding:interceptor',
          readiness: 'unresolved',
        }],
      });
      expect(catalogEntry(catalog, 'action', 'native_action').callbacks).toEqual([{
        slot: 'handler',
        bindingId: null,
        readiness: 'unbound',
      }]);

      const exported = await om.exportBehaviorManifestJson(runtime);
      expect(exported).toEqual(om.encodeBehaviorManifestJson(catalog));
      const decoded = om.decodeBehaviorManifestJson(exported);
      expect(decoded.success).toBe(true);
      expect(decoded.diagnostics).toEqual([]);
      expect(om.encodeBehaviorManifestJson(decoded.catalog)).toEqual(exported);
    } finally {
      db.close();
      om.clearRegistry(runtime);
    }
  });

  test('canonical bytes match the C# V1 golden for order, nulls, Unicode, and escaping', () => {
    const catalog = {
      behaviors: [
        {
          kind: 'interceptor',
          ownerType: 'Owner',
          name: 'action',
          constraintType: null,
          message: null,
          description: 'after',
          interceptorPhase: 'after',
          interceptorSeq: 7,
          callbacks: [{
            slot: 'handler',
            bindingId: 'binding:interceptor',
            readiness: 'unresolved',
          }],
        },
        {
          kind: 'mutation',
          ownerType: 'Owner',
          name: 'mutation',
          constraintType: null,
          message: null,
          description: 'mutation',
          interceptorPhase: null,
          interceptorSeq: null,
          callbacks: [{ slot: 'executor', bindingId: null, readiness: 'unbound' }],
        },
        {
          kind: 'action',
          ownerType: 'Owner',
          name: 'action',
          constraintType: null,
          message: null,
          description: 'action',
          interceptorPhase: null,
          interceptorSeq: null,
          callbacks: [{ slot: 'handler', bindingId: 'binding:action', readiness: 'ready' }],
        },
        {
          kind: 'computed',
          ownerType: 'Owner',
          name: 'computed',
          constraintType: null,
          message: null,
          description: 'tab\tvalue',
          interceptorPhase: null,
          interceptorSeq: null,
          callbacks: [{ slot: 'compute', bindingId: null, readiness: 'unbound' }],
        },
        {
          kind: 'constraint',
          ownerType: 'Owner中文',
          name: 'constraint"\\\n',
          constraintType: 'conditional',
          message: "message <>&'+`😀",
          description: null,
          interceptorPhase: null,
          interceptorSeq: null,
          callbacks: [
            { slot: 'validator', bindingId: null, readiness: 'unbound' },
            { slot: 'then', bindingId: 'binding:then', readiness: 'unresolved' },
            { slot: 'when', bindingId: 'binding:when', readiness: 'ready' },
          ],
        },
      ],
    };
    const csharpGolden = String.raw`{"version":1,"behaviors":[{"kind":"constraint","ownerType":"Owner\u4E2D\u6587","name":"constraint\u0022\\\n","constraintType":"conditional","message":"message \u003C\u003E\u0026\u0027\u002B\u0060\uD83D\uDE00","description":null,"interceptorPhase":null,"interceptorSeq":null,"callbacks":[{"slot":"when","bindingId":"binding:when","readiness":"ready"},{"slot":"then","bindingId":"binding:then","readiness":"unresolved"},{"slot":"validator","bindingId":null,"readiness":"unbound"}]},{"kind":"computed","ownerType":"Owner","name":"computed","constraintType":null,"message":null,"description":"tab\tvalue","interceptorPhase":null,"interceptorSeq":null,"callbacks":[{"slot":"compute","bindingId":null,"readiness":"unbound"}]},{"kind":"action","ownerType":"Owner","name":"action","constraintType":null,"message":null,"description":"action","interceptorPhase":null,"interceptorSeq":null,"callbacks":[{"slot":"handler","bindingId":"binding:action","readiness":"ready"}]},{"kind":"mutation","ownerType":"Owner","name":"mutation","constraintType":null,"message":null,"description":"mutation","interceptorPhase":null,"interceptorSeq":null,"callbacks":[{"slot":"executor","bindingId":null,"readiness":"unbound"}]},{"kind":"interceptor","ownerType":"Owner","name":"action","constraintType":null,"message":null,"description":"after","interceptorPhase":"after","interceptorSeq":7,"callbacks":[{"slot":"handler","bindingId":"binding:interceptor","readiness":"unresolved"}]}]}`;

    const encoded = new TextDecoder().decode(om.encodeBehaviorManifestJson(catalog));
    expect(encoded).toBe(csharpGolden);

    const reordered = JSON.stringify({
      behaviors: [...catalog.behaviors].reverse().map((entry) => ({
        callbacks: [...entry.callbacks].reverse(),
        interceptorSeq: entry.interceptorSeq,
        interceptorPhase: entry.interceptorPhase,
        description: entry.description,
        message: entry.message,
        constraintType: entry.constraintType,
        name: entry.name,
        ownerType: entry.ownerType,
        kind: entry.kind,
      })),
      version: 1,
    });
    const decoded = om.decodeBehaviorManifestJson(reordered);
    expect(decoded.success).toBe(true);
    expect(new TextDecoder().decode(om.encodeBehaviorManifestJson(decoded.catalog))).toBe(
      csharpGolden
    );
  });

  test('canonical bytes replace unpaired UTF-16 surrogates like System.Text.Json', () => {
    const cases = [
      { name: '\uD800', wireName: String.raw`\uFFFD` },
      { name: '\uDC00', wireName: String.raw`\uFFFD` },
      { name: '\uD800A', wireName: String.raw`\uFFFDA` },
      { name: '\uD83D\uDE00', wireName: String.raw`\uD83D\uDE00` },
    ];

    for (const { name, wireName } of cases) {
      const encoded = new TextDecoder().decode(om.encodeBehaviorManifestJson({
        behaviors: [{
          kind: 'action',
          ownerType: 'Owner',
          name,
          constraintType: null,
          message: null,
          description: null,
          interceptorPhase: null,
          interceptorSeq: null,
          callbacks: [],
        }],
      }));
      expect(encoded).toBe(
        `{"version":1,"behaviors":[{"kind":"action","ownerType":"Owner","name":"${wireName}",`
        + '"constraintType":null,"message":null,"description":null,'
        + '"interceptorPhase":null,"interceptorSeq":null,"callbacks":[]}]}'
      );
    }
  });

  test('decode returns the C# V1 OMM matrix with deterministic duplicate diagnostics', () => {
    const invalid = {
      version: 2,
      behaviors: [
        {
          kind: 'mystery',
          ownerType: ' ',
          name: 'bad',
          constraintType: null,
          message: null,
          description: null,
          interceptorPhase: null,
          interceptorSeq: null,
          callbacks: [{
            slot: 'mystery-slot',
            bindingId: 'binding:a',
            readiness: 'mystery-readiness',
          }],
        },
        {
          kind: 'action',
          ownerType: 'PortableOwner',
          name: 'duplicate',
          constraintType: 'forbidden',
          message: 'forbidden',
          description: 'first',
          interceptorPhase: null,
          interceptorSeq: null,
          callbacks: [
            { slot: 'handler', bindingId: null, readiness: 'ready' },
            { slot: 'handler', bindingId: 'binding:b', readiness: 'unresolved' },
          ],
        },
        {
          kind: 'action',
          ownerType: 'PortableOwner',
          name: 'duplicate',
          constraintType: null,
          message: null,
          description: 'conflict',
          interceptorPhase: null,
          interceptorSeq: null,
          callbacks: [{
            slot: 'compute',
            bindingId: 'binding:c',
            readiness: 'unresolved',
          }],
        },
        {
          kind: 'interceptor',
          ownerType: 'PortableOwner',
          name: 'bad_interceptor',
          constraintType: null,
          message: null,
          description: 'invalid key',
          interceptorPhase: 'during',
          interceptorSeq: -1,
          callbacks: [{ slot: 'handler', bindingId: null, readiness: 'unbound' }],
        },
      ],
    };
    const result = om.decodeBehaviorManifestJson(JSON.stringify(invalid));
    expect(result.success).toBe(false);
    expect(result.catalog).toBeNull();
    expect(new Set(result.diagnostics.map((diagnostic) => diagnostic.code))).toEqual(
      new Set([
        'OMM1002',
        'OMM1101',
        'OMM1102',
        'OMM1103',
        'OMM1201',
        'OMM1202',
        'OMM1203',
        'OMM1301',
        'OMM1302',
      ])
    );
    expect(result.diagnostics).toEqual([...result.diagnostics].sort((left, right) =>
      left.code.localeCompare(right.code)
      || left.path.localeCompare(right.path)
      || left.message.localeCompare(right.message)
    ));
    expect(result.diagnostics.filter((diagnostic) => diagnostic.code.startsWith('OMM13')))
      .toEqual([
        {
          code: 'OMM1301',
          path: '$.behaviors[1].callbacks[1]',
          message: "Callback binding key 'action:PortableOwner/duplicate/handler' is duplicated or conflicting.",
        },
        {
          code: 'OMM1302',
          path: '$.behaviors[2]',
          message: "Behavior key 'action:PortableOwner/duplicate' is duplicated or conflicting.",
        },
      ]);

    const invalidMetadataMatrix = {
      version: 1,
      behaviors: [
        {
          kind: 'constraint',
          ownerType: 'PortableOwner',
          name: 'invalid_constraint',
          constraintType: null,
          message: 'allowed',
          description: 'forbidden',
          interceptorPhase: 'before',
          interceptorSeq: 0,
          callbacks: [],
        },
        {
          kind: 'computed',
          ownerType: 'PortableOwner',
          name: 'invalid_computed',
          constraintType: 'custom',
          message: 'forbidden',
          description: 'allowed',
          interceptorPhase: null,
          interceptorSeq: null,
          callbacks: [],
        },
        {
          kind: 'action',
          ownerType: 'PortableOwner',
          name: 'invalid_action',
          constraintType: 'custom',
          message: 'forbidden',
          description: 'allowed',
          interceptorPhase: null,
          interceptorSeq: null,
          callbacks: [],
        },
        {
          kind: 'mutation',
          ownerType: 'PortableOwner',
          name: 'invalid_mutation',
          constraintType: 'custom',
          message: 'forbidden',
          description: 'allowed',
          interceptorPhase: 'after',
          interceptorSeq: 2,
          callbacks: [],
        },
        {
          kind: 'interceptor',
          ownerType: 'PortableOwner',
          name: 'invalid_interceptor',
          constraintType: 'custom',
          message: 'forbidden',
          description: 'allowed',
          interceptorPhase: 'after',
          interceptorSeq: 3,
          callbacks: [],
        },
      ],
    };
    expect(om.decodeBehaviorManifestJson(JSON.stringify(invalidMetadataMatrix)).diagnostics)
      .toEqual([
        {
          code: 'OMM1203',
          path: '$.behaviors[0].constraintType',
          message: "Property 'constraintType' must be a string for behavior kind 'constraint'.",
        },
        {
          code: 'OMM1203',
          path: '$.behaviors[0].description',
          message: "Property 'description' must be null for behavior kind 'constraint'.",
        },
        {
          code: 'OMM1203',
          path: '$.behaviors[0].interceptorPhase',
          message: "Property 'interceptorPhase' must be null for behavior kind 'constraint'.",
        },
        {
          code: 'OMM1203',
          path: '$.behaviors[0].interceptorSeq',
          message: "Property 'interceptorSeq' must be null for behavior kind 'constraint'.",
        },
        {
          code: 'OMM1203',
          path: '$.behaviors[1].constraintType',
          message: "Property 'constraintType' must be null for behavior kind 'computed'.",
        },
        {
          code: 'OMM1203',
          path: '$.behaviors[1].message',
          message: "Property 'message' must be null for behavior kind 'computed'.",
        },
        {
          code: 'OMM1203',
          path: '$.behaviors[2].constraintType',
          message: "Property 'constraintType' must be null for behavior kind 'action'.",
        },
        {
          code: 'OMM1203',
          path: '$.behaviors[2].message',
          message: "Property 'message' must be null for behavior kind 'action'.",
        },
        {
          code: 'OMM1203',
          path: '$.behaviors[3].constraintType',
          message: "Property 'constraintType' must be null for behavior kind 'mutation'.",
        },
        {
          code: 'OMM1203',
          path: '$.behaviors[3].interceptorPhase',
          message: "Property 'interceptorPhase' must be null for behavior kind 'mutation'.",
        },
        {
          code: 'OMM1203',
          path: '$.behaviors[3].interceptorSeq',
          message: "Property 'interceptorSeq' must be null for behavior kind 'mutation'.",
        },
        {
          code: 'OMM1203',
          path: '$.behaviors[3].message',
          message: "Property 'message' must be null for behavior kind 'mutation'.",
        },
        {
          code: 'OMM1203',
          path: '$.behaviors[4].constraintType',
          message: "Property 'constraintType' must be null for behavior kind 'interceptor'.",
        },
        {
          code: 'OMM1203',
          path: '$.behaviors[4].message',
          message: "Property 'message' must be null for behavior kind 'interceptor'.",
        },
      ]);

    expect(om.decodeBehaviorManifestJson(null)).toMatchObject({
      success: false,
      diagnostics: [{ code: 'OMM1000', path: '$' }],
    });
    expect(om.decodeBehaviorManifestJson('{ not-json')).toMatchObject({
      success: false,
      diagnostics: [{ code: 'OMM1000', path: '$' }],
    });
    expect(om.decodeBehaviorManifestJson('[]')).toEqual({
      catalog: null,
      diagnostics: [{
        code: 'OMM1001',
        path: '$',
        message: 'Manifest root must be an object.',
      }],
      success: false,
    });
    expect(om.decodeBehaviorManifestJson('{"version":"1","behaviors":[]}')).toMatchObject({
      success: false,
      diagnostics: [{ code: 'OMM1001', path: '$.version' }],
    });
  });
});
