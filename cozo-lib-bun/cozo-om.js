const { createHash } = require('crypto');

const ALLOWED_VALUE_TYPES = new Set(['String', 'Number', 'Bool', 'Json', 'Validity']);
const dsl = require('./cozo-dsl');
const { query, param } = dsl;

// In-memory registries for Phase 2+ behavior layer.
// Handlers are JavaScript functions and are NOT persisted in CozoDB.
const _actionRegistry = new Map();
const _mutationRegistry = new Map();
const _interceptorRegistry = new Map();
const _constraintRegistry = new Map();
const _computedRegistry = new Map();

function clearRegistry() {
  _actionRegistry.clear();
  _mutationRegistry.clear();
  _interceptorRegistry.clear();
  _constraintRegistry.clear();
  _computedRegistry.clear();
}

function ensureRunner(runner) {
  if (!runner || typeof runner.run !== 'function') {
    throw new Error('Runner must provide a run(script, params?) method');
  }
}

async function runRows(runner, script, params) {
  ensureRunner(runner);
  const result = await runner.run(script, params || {});
  return result && Array.isArray(result.rows) ? result.rows : [];
}

async function runDslRows(runner, builder) {
  const { script, params } = builder.build();
  return runRows(runner, script, params);
}

function _isStoredRelationMissingError(e) {
  const code = e && typeof e === 'object' ? e.code : '';
  return code === 'query::relation_not_found' || code === 'eval::stored_relation_not_found';
}

async function _runDslCreateIgnoreConflict(runner, builder) {
  try {
    await runDslRows(runner, builder);
    return { created: true };
  } catch (e) {
    if (e && typeof e === 'object' && e.code === 'eval::stored_relation_conflict') {
      return { created: false };
    }
    throw e;
  }
}

async function _hasNamedFields(runner, relationName, fieldNames) {
  const rel = String(relationName || '').trim();
  const names = Array.isArray(fieldNames) ? fieldNames.map((n) => String(n || '').trim()).filter(Boolean) : [];
  if (!rel) throw new Error('relationName is required');
  if (!names.length) throw new Error('fieldNames must be a non-empty array');

  // Avoid sys ops (e.g. ::columns) so this works in multiTransact runners.
  const fields = names.join(', ');
  try {
    await runRows(
      runner,
      `
?[x] :=
  *${rel}{ ${fields} },
  x = 1
:limit 1
      `.trim(),
      {}
    );
    return true;
  } catch (e) {
    const code = e && typeof e === 'object' ? e.code : '';
    if (code === 'eval::named_field_not_found' || code === 'eval::required_col_not_found') {
      return false;
    }
    if (code === 'query::relation_not_found' || code === 'eval::stored_relation_not_found') {
      return false;
    }
    throw e;
  }
}

async function _migrateOmPropertyToBiTemporal(runner) {
  const txTime = new Date().toISOString();
  await runRows(
    runner,
    `
?[entity_id, attr_name, valid_time, value, tx_time] :=
  *om_property{ entity_id, attr_name, value },
  valid_time = "ASSERT",
  tx_time = $tx_time_param

:replace om_property { entity_id: String, attr_name: String, valid_time: Validity => value, tx_time: String }
    `.trim(),
    { tx_time_param: txTime }
  );
}

async function _migrateOmEdgeToBiTemporal(runner) {
  const txTime = new Date().toISOString();
  await runRows(
    runner,
    `
?[from_id, rel_name, to_id, valid_time, props, tx_time] :=
  *om_edge{ from_id, rel_name, to_id, props },
  valid_time = "ASSERT",
  tx_time = $tx_time_param

:replace om_edge { from_id: String, rel_name: String, to_id: String, valid_time: Validity => props, tx_time: String }
    `.trim(),
    { tx_time_param: txTime }
  );
}

function inferValueType(value) {
  if (typeof value === 'string') {
    return 'String';
  }
  if (typeof value === 'number' && Number.isFinite(value)) {
    return 'Number';
  }
  if (typeof value === 'boolean') {
    return 'Bool';
  }
  if (value !== null && typeof value === 'object') {
    return 'Json';
  }
  return 'Unknown';
}

function _normalizeValidityInput(value) {
  // Returns { tsUs: number, isAssert: boolean } if value is a supported validity-like input.
  // Supported:
  // - RFC 3339 string (optionally prefixed with '~' for retract)
  // - "ASSERT" / "RETRACT" (uses current wall-clock time)
  // - [microseconds, bool]
  if (typeof value === 'string') {
    const raw = String(value).trim();
    if (!raw) return null;
    if (raw === 'ASSERT' || raw === 'RETRACT') {
      return { tsUs: Date.now() * 1000, isAssert: raw === 'ASSERT' };
    }
    const isAssert = !raw.startsWith('~');
    const tsStr = isAssert ? raw : raw.slice(1);
    const ms = Date.parse(tsStr);
    if (!Number.isFinite(ms)) return null;
    return { tsUs: ms * 1000, isAssert };
  }
  if (Array.isArray(value) && value.length === 2) {
    const tsUs = value[0];
    const isAssert = value[1];
    if (typeof tsUs !== 'number' || !Number.isFinite(tsUs) || !Number.isInteger(tsUs)) return null;
    if (typeof isAssert !== 'boolean') return null;
    return { tsUs, isAssert };
  }
  return null;
}

function _getOrCreateTypeMap(registry, typeName) {
  const key = String(typeName || '').trim();
  if (!key) throw new Error('Type name is required');
  if (!registry.has(key)) registry.set(key, new Map());
  return registry.get(key);
}

async function _ensureBehaviorTypeExists(runner, typeName) {
  const name = String(typeName || '').trim();
  if (!name) throw new Error('Type name is required');
  const isType = await _typeExists(runner, name);
  if (isType) return;
  throw new Error(`Type '${name}' does not exist`);
}

async function _resolveByAncestors(runner, typeName, lookupFn) {
  const chain = [typeName, ...(await _getAncestorList(runner, typeName))];
  for (const t of chain) {
    const hit = lookupFn(t);
    if (hit) return hit;
  }
  return null;
}

function _normalizeConstraintType(scope) {
  const s = String(scope || '').trim().toLowerCase();
  if (!s || s === 'conditional') return 'conditional';
  if (s === 'cross-entity' || s === 'cross_entity') return 'cross-entity';
  if (s === 'computed-dep' || s === 'computed_dep') return 'computed-dep';
  throw new Error(`Unsupported constraint scope '${scope}'`);
}

async function defineConstraint(runner, typeName, constraintName, def) {
  const tn = String(typeName || '').trim();
  const cn = String(constraintName || '').trim();
  if (!tn) throw new Error('Type name is required');
  if (!cn) throw new Error('Constraint name is required');
  if (!def || typeof def !== 'object') {
    throw new Error('Constraint definition must be an object');
  }

  const constraintType = _normalizeConstraintType(def.scope);
  const message = def.message != null ? String(def.message) : '';
  const whenFn = def.when;
  const thenFn = def.then;

  if (typeof whenFn !== 'function') {
    throw new Error('Constraint definition must provide a when(ctx) function');
  }
  if (typeof thenFn !== 'function') {
    throw new Error('Constraint definition must provide a then(ctx) function');
  }

  await _ensureBehaviorTypeExists(runner, tn);

  await runDslRows(
    runner,
    query()
      .input({
        type_name: param('type_name', tn),
        constraint_name: param('constraint_name', cn),
        constraint_type: param('constraint_type', constraintType),
        message: param('message', message),
      })
      .put('om_constraint_def', ['type_name', 'constraint_name'], ['constraint_type', 'message'])
  );

  const typeMap = _getOrCreateTypeMap(_constraintRegistry, tn);
  typeMap.set(cn, {
    constraintType,
    message,
    when: whenFn,
    then: thenFn,
    ownerType: tn,
  });
}

async function defineComputed(runner, typeName, attrName, computeFn, description = '') {
  if (typeof computeFn !== 'function') {
    throw new Error('Computed function must be a function');
  }
  const tn = String(typeName || '').trim();
  const an = String(attrName || '').trim();
  if (!tn) throw new Error('Type name is required');
  if (!an) throw new Error('Attribute name is required');

  await _ensureBehaviorTypeExists(runner, tn);

  await runDslRows(
    runner,
    query()
      .input({
        type_name: param('type_name', tn),
        attr_name: param('attr_name', an),
        description: param('description', String(description || '')),
      })
      .put('om_computed_def', ['type_name', 'attr_name'], ['description'])
  );

  const typeMap = _getOrCreateTypeMap(_computedRegistry, tn);
  typeMap.set(an, {
    computeFn,
    description: String(description || ''),
    ownerType: tn,
  });
}

async function _resolveComputedDef(runner, typeName, attrName) {
  const tn = String(typeName || '').trim();
  const an = String(attrName || '').trim();
  if (!tn) throw new Error('Type name is required');
  if (!an) throw new Error('Attribute name is required');

  return _resolveByAncestors(
    runner,
    tn,
    (t) => {
      const map = _computedRegistry.get(t);
      return map && map.get(an);
    }
  );
}

async function _getComputedMapForType(runner, typeName) {
  const tn = String(typeName || '').trim();
  if (!tn) throw new Error('Type name is required');
  const ancestors = await _getAncestorList(runner, tn);
  const chain = [...ancestors].reverse();

  const merged = new Map();
  for (const t of chain) {
    const map = _computedRegistry.get(t);
    if (!map) continue;
    for (const [attrName, def] of map.entries()) {
      merged.set(attrName, def);
    }
  }
  const self = _computedRegistry.get(tn);
  if (self) {
    for (const [attrName, def] of self.entries()) {
      merged.set(attrName, def);
    }
  }
  return merged;
}

async function validateConstraints(runner, entityId, options) {
  const id = String(entityId || '').trim();
  if (!id) throw new Error('entityId is required');

  const wantedTypes = options && Array.isArray(options.types) ? options.types : null;
  const wantedSet = wantedTypes ? new Set(wantedTypes.map((t) => String(t))) : null;

  const typeName = await getEntityType(runner, id);
  const ancestors = await _getAncestorList(runner, typeName);
  const chain = [typeName, ...ancestors].reverse();

  const ctx = {
    runner,
    entityId: id,
    typeName,
    getProperty: async (attrName) => getProperty(runner, id, attrName),
    getNeighbors: async (relName, direction) => getNeighbors(runner, id, relName, direction),
  };

  const errors = [];

  for (const t of chain) {
    const map = _constraintRegistry.get(t);
    if (!map) continue;
    for (const [name, def] of map.entries()) {
      if (wantedSet && !wantedSet.has(def.constraintType)) continue;
      let active = false;
      try {
        active = await def.when(ctx);
      } catch (e) {
        errors.push(`Constraint '${name}' evaluation failed (when): ${e.message || e}`);
        continue;
      }
      if (!active) continue;
      let ok = false;
      try {
        ok = await def.then(ctx);
      } catch (e) {
        errors.push(`Constraint '${name}' evaluation failed (then): ${e.message || e}`);
        continue;
      }
      if (!ok) {
        const msg = def.message ? `: ${def.message}` : '';
        errors.push(`Constraint '${name}' violated${msg}`);
      }
    }
  }

  return { valid: errors.length === 0, errors };
}

async function _withWriteTxIfPossible(runner, fn) {
  if (runner && typeof runner.multiTransact === 'function') {
    const tx = runner.multiTransact(true);
    try {
      const result = await fn(tx);
      tx.commit();
      return result;
    } catch (error) {
      try {
        tx.abort();
      } catch (_) {
      }
      throw error;
    }
  }
  return fn(runner);
}

async function defineMutation(runner, typeName, mutationName, executor, description = '') {
  if (typeof executor !== 'function') {
    throw new Error('Mutation executor must be a function');
  }
  const tn = String(typeName || '').trim();
  const mn = String(mutationName || '').trim();
  if (!tn) throw new Error('Type name is required');
  if (!mn) throw new Error('Mutation name is required');

  await _ensureBehaviorTypeExists(runner, tn);

  await runDslRows(
    runner,
    query()
      .input({
        type_name: param('type_name', tn),
        mutation_name: param('mutation_name', mn),
        description: param('description', String(description || '')),
      })
      .put('om_mutation_def', ['type_name', 'mutation_name'], ['description'])
  );

  const typeMap = _getOrCreateTypeMap(_mutationRegistry, tn);
  typeMap.set(mn, { executor, description: String(description || '') });
}

async function defineAction(runner, typeName, actionName, handler, description = '') {
  if (typeof handler !== 'function') {
    throw new Error('Action handler must be a function');
  }
  const tn = String(typeName || '').trim();
  const an = String(actionName || '').trim();
  if (!tn) throw new Error('Type name is required');
  if (!an) throw new Error('Action name is required');

  await _ensureBehaviorTypeExists(runner, tn);

  await runDslRows(
    runner,
    query()
      .input({
        type_name: param('type_name', tn),
        action_name: param('action_name', an),
        description: param('description', String(description || '')),
      })
      .put('om_action_def', ['type_name', 'action_name'], ['description'])
  );

  const typeMap = _getOrCreateTypeMap(_actionRegistry, tn);
  typeMap.set(an, { handler, description: String(description || ''), ownerType: tn });
}

async function _resolveActionDef(runner, typeName, actionName) {
  const tn = String(typeName || '').trim();
  const an = String(actionName || '').trim();
  if (!tn) throw new Error('Type name is required');
  if (!an) throw new Error('Action name is required');

  return _resolveByAncestors(
    runner,
    tn,
    (t) => {
      const map = _actionRegistry.get(t);
      return map && map.get(an);
    }
  );
}

async function callParentAction(ctx, actionName, params) {
  if (!ctx || typeof ctx !== 'object') {
    throw new Error('Action context is required');
  }
  const runner = ctx.runner;
  const entityId = String(ctx.entityId || '').trim();
  const entityTypeName = String(ctx.typeName || '').trim();
  const currentOwnerType = String(ctx.actionOwnerType || ctx.typeName || '').trim();
  const an = String(actionName || '').trim();
  if (!runner || typeof runner.run !== 'function') {
    throw new Error('Action context runner is required');
  }
  if (!entityId) throw new Error('Action context entityId is required');
  if (!entityTypeName) throw new Error('Action context typeName is required');
  if (!currentOwnerType) throw new Error('Action context actionOwnerType is required');
  if (!an) throw new Error('Action name is required');

  const parentType = await _getParentType(runner, currentOwnerType);
  if (!parentType) {
    throw new Error(`Action '${an}' has no parent action (type '${currentOwnerType}' has no parentType)`);
  }

  const chain = [parentType, ...(await _getAncestorList(runner, parentType))];
  let parentDef = null;
  for (const t of chain) {
    const map = _actionRegistry.get(t);
    const hit = map && map.get(an);
    if (hit) {
      parentDef = hit;
      break;
    }
  }
  if (!parentDef) {
    throw new Error(`Parent action '${an}' not defined for type '${currentOwnerType}'`);
  }

  const base = {
    runner,
    entityId,
    typeName: entityTypeName,
    getProperty: ctx.getProperty,
    setProperty: ctx.setProperty,
    linkEntities: ctx.linkEntities,
    getNeighbors: ctx.getNeighbors,
  };

  const nextCtx = {
    ...base,
    params: params || {},
    actionOwnerType: parentDef.ownerType || parentType,
  };
  nextCtx.callParentAction = (name, p) => callParentAction(nextCtx, name, p);

  const mutations = await parentDef.handler(nextCtx, nextCtx.params);
  const list = mutations == null ? [] : mutations;
  if (!Array.isArray(list)) {
    throw new Error(`Action '${an}' must return an array of mutations`);
  }
  return list;
}

function _getOrCreateActionEntry(registry, typeName, actionName) {
  const tn = String(typeName || '').trim();
  const an = String(actionName || '').trim();
  if (!tn) throw new Error('Type name is required');
  if (!an) throw new Error('Action name is required');
  const typeMap = _getOrCreateTypeMap(registry, tn);
  if (!typeMap.has(an)) {
    typeMap.set(an, { before: [], after: [] });
  }
  return typeMap.get(an);
}

function _normalizeInterceptorPhase(phase) {
  const p = String(phase || '').trim().toLowerCase();
  if (p !== 'before' && p !== 'after') {
    throw new Error("Interceptor phase must be 'before' or 'after'");
  }
  return p;
}

async function addInterceptor(runner, typeName, actionName, phase, handler, description = '') {
  if (typeof handler !== 'function') {
    throw new Error('Interceptor handler must be a function');
  }
  const tn = String(typeName || '').trim();
  const an = String(actionName || '').trim();
  const ph = _normalizeInterceptorPhase(phase);
  if (!tn) throw new Error('Type name is required');
  if (!an) throw new Error('Action name is required');

  await _ensureBehaviorTypeExists(runner, tn);

  const entry = _getOrCreateActionEntry(_interceptorRegistry, tn, an);
  const seq = entry[ph].length;

  await runDslRows(
    runner,
    query()
      .input({
        type_name: param('type_name', tn),
        action_name: param('action_name', an),
        phase: param('phase', ph),
        seq: param('seq', seq),
        description: param('description', String(description || '')),
      })
      .put('om_interceptor_def', ['type_name', 'action_name', 'phase', 'seq'], ['description'])
  );

  entry[ph].push({ handler, seq, description: String(description || ''), ownerType: tn });
}

async function _collectInterceptors(runner, entityTypeName, actionName) {
  const tn = String(entityTypeName || '').trim();
  const an = String(actionName || '').trim();
  if (!tn) throw new Error('Type name is required');
  if (!an) throw new Error('Action name is required');

  // Apply ancestor interceptors first.
  const chain = [tn, ...(await _getAncestorList(runner, tn))].reverse();

  const before = [];
  const after = [];

  for (const t of chain) {
    const typeMap = _interceptorRegistry.get(t);
    if (!typeMap) continue;
    const entry = typeMap.get(an);
    if (!entry) continue;
    for (const it of entry.before || []) before.push(it);
    for (const it of entry.after || []) after.push(it);
  }

  return { before, after };
}

async function executeAction(db, entityId, actionName, params) {
  if (!db || typeof db.multiTransact !== 'function') {
    throw new Error('executeAction requires a CozoDb instance with multiTransact(write)');
  }

  const id = String(entityId || '').trim();
  const an = String(actionName || '').trim();
  if (!id) throw new Error('entityId is required');
  if (!an) throw new Error('Action name is required');

  const tx = db.multiTransact(true);
  try {
    const typeName = await getEntityType(tx, id);
    const interceptors = await _collectInterceptors(tx, typeName, an);
    const def = await _resolveActionDef(tx, typeName, an);
    if (!def) {
      throw new Error(`Action '${an}' not defined for type '${typeName}'`);
    }

    const ctx = {
      runner: tx,
      entityId: id,
      typeName,
      actionOwnerType: def.ownerType || typeName,
      params: params || {},
      // These helpers are intended for future phases; for now they support basic handlers.
      getProperty: async (attrName, options) => {
        const opts = options && typeof options === 'object' ? options : {};
        const asOf = Object.prototype.hasOwnProperty.call(opts, 'asOf') ? String(opts.asOf || '').trim() : '';
        if (asOf) {
          return getPropertyAsOf(tx, id, attrName, asOf);
        }
        return getProperty(tx, id, attrName);
      },
      setProperty: async (attrName, value, options) => setProperty(tx, id, attrName, value, options),
      linkEntities: async (relName, toId, props, options) => linkEntities(tx, id, relName, toId, props, options),
      getNeighbors: async (relName, direction) => getNeighbors(tx, id, relName, direction),
    };

    ctx.callParentAction = (name, p) => callParentAction(ctx, name, p);

    for (const it of interceptors.before) {
      await it.handler(ctx);
    }

    const mutations = await def.handler(ctx, ctx.params);
    const list = mutations == null ? [] : mutations;
    if (!Array.isArray(list)) {
      throw new Error(`Action '${an}' must return an array of mutations`);
    }

    await _executeMutationsInRunner(tx, id, list);

    for (const it of interceptors.after) {
      await it.handler(ctx);
    }
    tx.commit();
  } catch (error) {
    try {
      tx.abort();
    } catch (_) {
    }
    throw error;
  }
}

async function _executeMutationsInRunner(runner, entityId, mutations) {
  const list = Array.isArray(mutations) ? mutations : [];
  const id = String(entityId || '').trim();
  if (!id) throw new Error('entityId is required');

  const typeName = await getEntityType(runner, id);

  const ctx = {
    runner,
    entityId: id,
    typeName,
    getProperty: async (attrName, options) => {
      const opts = options && typeof options === 'object' ? options : {};
      const asOf = Object.prototype.hasOwnProperty.call(opts, 'asOf') ? String(opts.asOf || '').trim() : '';
      if (asOf) {
        return getPropertyAsOf(runner, id, attrName, asOf);
      }
      return getProperty(runner, id, attrName);
    },
    setProperty: async (attrName, value, options) => setProperty(runner, id, attrName, value, options),
    linkEntities: async (relName, toId, props, options) => linkEntities(runner, id, relName, toId, props, options),
    getNeighbors: async (relName, direction) => getNeighbors(runner, id, relName, direction),
  };

  for (const item of list) {
    const mutation = item && typeof item === 'object' ? String(item.mutation || '').trim() : '';
    const paramsObj = item && typeof item === 'object' ? (item.params || {}) : {};
    if (!mutation) throw new Error('Mutation item missing mutation name');

    const resolved = await _resolveByAncestors(
      runner,
      typeName,
      (t) => {
        const map = _mutationRegistry.get(t);
        return map && map.get(mutation);
      }
    );
    if (!resolved) {
      throw new Error(`Mutation '${mutation}' not defined for type '${typeName}'`);
    }
    await resolved.executor(ctx, paramsObj);
  }
}

async function executeMutations(db, entityId, mutations) {
  if (!db || typeof db.multiTransact !== 'function') {
    throw new Error('executeMutations requires a CozoDb instance with multiTransact(write)');
  }
  const tx = db.multiTransact(true);
  try {
    await _executeMutationsInRunner(tx, entityId, mutations);
    tx.commit();
  } catch (error) {
    try {
      tx.abort();
    } catch (_) {
    }
    throw error;
  }
}

async function initSchema(runner) {
  await _runDslCreateIgnoreConflict(runner, query().create('om_type', ['name'], ['description', 'parent_type']));
  await _runDslCreateIgnoreConflict(runner, query().create('om_mixin', ['name'], ['description']));
  await _runDslCreateIgnoreConflict(runner, query().create('om_type_mixin', ['type_name', 'mixin_name'], []));

  // Phase 2+ behavior layer metadata (handlers are registered in JS, these relations store definitions)
  await _runDslCreateIgnoreConflict(
    runner,
    query().create('om_action_def', ['type_name', 'action_name'], ['description'])
  );
  await _runDslCreateIgnoreConflict(
    runner,
    query().create('om_mutation_def', ['type_name', 'mutation_name'], ['description'])
  );
  await _runDslCreateIgnoreConflict(
    runner,
    query().create('om_interceptor_def', ['type_name', 'action_name', 'phase', 'seq'], ['description'])
  );
  await _runDslCreateIgnoreConflict(
    runner,
    query().create('om_constraint_def', ['type_name', 'constraint_name'], ['constraint_type', 'message'])
  );
  await _runDslCreateIgnoreConflict(
    runner,
    query().create('om_computed_def', ['type_name', 'attr_name'], ['description'])
  );

  // P3/WAVE-P3-01 (T3.1.2): permission policy metadata.
  // These are stored relations (not sys ops) so they work in multiTransact runners.
  await _runDslCreateIgnoreConflict(runner, query().create('om_perm_action', ['action'], ['description']));
  await _runDslCreateIgnoreConflict(
    runner,
    query().create('om_perm_policy', ['policy_id'], ['effect', 'action', 'resource_type', 'enabled', 'description'])
  );
  await _runDslCreateIgnoreConflict(
    runner,
    query().create('om_perm_abac_rule', ['policy_id', 'left_ref', 'op', 'right_ref'], [])
  );
  await _runDslCreateIgnoreConflict(runner, query().create('om_perm_path_rule', ['policy_id', 'path'], []));

  await _runDslCreateIgnoreConflict(
    runner,
    query().create('om_attr_def', ['type_name', 'attr_name'], ['value_type', 'required'])
  );
  await _runDslCreateIgnoreConflict(
    runner,
    query().create('om_rel_def', ['rel_name'], ['from_type', 'to_type', 'directed'])
  );
  await _runDslCreateIgnoreConflict(runner, query().create('om_entity', ['id'], ['type_name', 'label']));
  // Bi-temporal properties: valid_time (Validity) as the last key column enables time-travel queries via @ timestamp.
  // tx_time stores the system record time for audit.
  const omPropertyCreated = await _runDslCreateIgnoreConflict(
    runner,
    query().createRaw(
      'om_property',
      'entity_id: String, attr_name: String, valid_time: Validity => value, tx_time: String'
    )
  );
  if (!omPropertyCreated.created) {
    const hasTemporal = await _hasNamedFields(runner, 'om_property', ['valid_time', 'tx_time']);
    if (!hasTemporal) {
      await _migrateOmPropertyToBiTemporal(runner);
    }
  }
  // Bi-temporal edges: valid_time (Validity) as the last key column enables time-travel queries via @ timestamp.
  // tx_time stores the system record time for audit.
  const omEdgeCreated = await _runDslCreateIgnoreConflict(
    runner,
    query().createRaw(
      'om_edge',
      'from_id: String, rel_name: String, to_id: String, valid_time: Validity => props, tx_time: String'
    )
  );
  if (!omEdgeCreated.created) {
    const hasTemporal = await _hasNamedFields(runner, 'om_edge', ['valid_time', 'tx_time']);
    if (!hasTemporal) {
      await _migrateOmEdgeToBiTemporal(runner);
    }
  }

  await _runDslCreateIgnoreConflict(
    runner,
    query().create('om_attr_desc', ['type_name', 'attr_name'], ['description'])
  );
  await _runDslCreateIgnoreConflict(
    runner,
    query().create('om_rel_desc', ['rel_name'], ['description'])
  );

  // P1/WAVE-P1-01: schema versioning + alias metadata.
  // These are stored relations (not sys ops) so they work in multiTransact runners.
  await _runDslCreateIgnoreConflict(
    runner,
    query().create('om_schema_state', ['id'], ['current_version', 'current_checksum'])
  );
  await _runDslCreateIgnoreConflict(
    runner,
    query().create(
      'om_schema_version',
      ['version'],
      ['created_at', 'label', 'description', 'parent_version', 'checksum']
    )
  );
  await _runDslCreateIgnoreConflict(
    runner,
    query().create(
      'om_schema_migration',
      ['migration_id'],
      ['from_version', 'to_version', 'applied_at', 'applied_by', 'status', 'error', 'summary_json']
    )
  );
  await _runDslCreateIgnoreConflict(
    runner,
    query().create('om_schema_snapshot', ['version'], ['snapshot_json'])
  );
  await _runDslCreateIgnoreConflict(runner, query().create('om_alias_type', ['alias'], ['canonical']));
  await _runDslCreateIgnoreConflict(runner, query().create('om_alias_rel', ['alias'], ['canonical']));
  await _runDslCreateIgnoreConflict(
    runner,
    query().create('om_alias_attr', ['type_name', 'alias_attr'], ['canonical_attr'])
  );

  // Existential rules (OM-024): declarative JSON specs, fully persisted (unlike JS-callback constraints).
  await _runDslCreateIgnoreConflict(
    runner,
    query().create('om_existential_rule_def', ['rule_name'], ['spec_json', 'mode', 'message', 'enabled'])
  );

  // Seed initial schema state (idempotent; do not overwrite existing state).
  const defaultSchemaId = 'default';
  const stateRows = await runDslRows(
    runner,
    query()
      .select(['current_version'])
      .fromStored('om_schema_state', {
        id: param('id', defaultSchemaId),
        current_version: dsl.var('current_version'),
        current_checksum: dsl.var('_c'),
      })
      .limit(1)
  );
  if (!stateRows.length) {
    try {
      await runDslRows(
        runner,
        query()
          .input({
            id: param('id', defaultSchemaId),
            current_version: param('current_version', 1),
            current_checksum: param('current_checksum', ''),
          })
          .insert('om_schema_state', ['id'], ['current_version', 'current_checksum'])
      );
    } catch (e) {
      const code = e && typeof e === 'object' ? e.code : '';
      if (code !== 'eval::key_conflict') throw e;
    }
  }

  const v1Rows = await runDslRows(
    runner,
    query()
      .select(['created_at'])
      .fromStored('om_schema_version', {
        version: param('version', 1),
        created_at: dsl.var('created_at'),
        label: dsl.var('_label'),
        description: dsl.var('_description'),
        parent_version: dsl.var('_parent_version'),
        checksum: dsl.var('_checksum'),
      })
      .limit(1)
  );
  if (!v1Rows.length) {
    const now = new Date().toISOString();
    try {
      await runDslRows(
        runner,
        query()
          .input({
            version: param('version', 1),
            created_at: param('created_at', now),
            label: param('label', 'v1'),
            description: param('description', ''),
            parent_version: param('parent_version', null),
            checksum: param('checksum', ''),
          })
          .insert(
            'om_schema_version',
            ['version'],
            ['created_at', 'label', 'description', 'parent_version', 'checksum']
          )
      );
    } catch (e) {
      const code = e && typeof e === 'object' ? e.code : '';
      if (code !== 'eval::key_conflict') throw e;
    }
  }
}

const createSchema = initSchema;

async function seedPermissionMetadata(runner, data) {
  const payload = data && typeof data === 'object' ? data : null;
  if (!payload) return;

  const actions = Array.isArray(payload.actions) ? payload.actions : [];
  const policies = Array.isArray(payload.policies) ? payload.policies : [];
  const abacRules = Array.isArray(payload.abacRules) ? payload.abacRules : [];
  const pathRules = Array.isArray(payload.pathRules) ? payload.pathRules : [];

  if (!actions.length && !policies.length && !abacRules.length && !pathRules.length) return;

  // Ensure permission metadata relations exist (idempotent).
  await _runDslCreateIgnoreConflict(runner, query().create('om_perm_action', ['action'], ['description']));
  await _runDslCreateIgnoreConflict(
    runner,
    query().create('om_perm_policy', ['policy_id'], ['effect', 'action', 'resource_type', 'enabled', 'description'])
  );
  await _runDslCreateIgnoreConflict(
    runner,
    query().create('om_perm_abac_rule', ['policy_id', 'left_ref', 'op', 'right_ref'], [])
  );
  await _runDslCreateIgnoreConflict(runner, query().create('om_perm_path_rule', ['policy_id', 'path'], []));

  for (const item of actions) {
    if (!item || typeof item !== 'object') continue;
    const action = String(item.action || '').trim();
    if (!action) continue;
    const description = item.description != null ? String(item.description) : '';
    await runDslRows(
      runner,
      query()
        .input({
          action: param('action', action),
          description: param('description', description),
        })
        .put('om_perm_action', ['action'], ['description'])
    );
  }

  for (const item of policies) {
    if (!item || typeof item !== 'object') continue;
    const policyId = String(item.policy_id ?? item.policyId ?? '').trim();
    if (!policyId) continue;
    const effect = item.effect != null ? String(item.effect) : '';
    const action = item.action != null ? String(item.action) : '';
    const resourceType = item.resource_type ?? item.resourceType;
    const enabled = Object.prototype.hasOwnProperty.call(item, 'enabled') ? !!item.enabled : true;
    const description = item.description != null ? String(item.description) : '';
    await runDslRows(
      runner,
      query()
        .input({
          policy_id: param('policy_id', policyId),
          effect: param('effect', effect),
          action: param('action', action),
          resource_type: param('resource_type', resourceType != null ? String(resourceType) : ''),
          enabled: param('enabled', enabled),
          description: param('description', description),
        })
        .put('om_perm_policy', ['policy_id'], ['effect', 'action', 'resource_type', 'enabled', 'description'])
    );
  }

  for (const item of abacRules) {
    if (!item || typeof item !== 'object') continue;
    const policyId = String(item.policy_id ?? item.policyId ?? '').trim();
    const leftRef = String(item.left_ref ?? item.leftRef ?? '').trim();
    const op = String(item.op ?? '').trim();
    const rightRef = String(item.right_ref ?? item.rightRef ?? '').trim();
    if (!policyId || !leftRef || !op || !rightRef) continue;
    await runDslRows(
      runner,
      query()
        .input({
          policy_id: param('policy_id', policyId),
          left_ref: param('left_ref', leftRef),
          op: param('op', op),
          right_ref: param('right_ref', rightRef),
        })
        .put('om_perm_abac_rule', ['policy_id', 'left_ref', 'op', 'right_ref'], [])
    );
  }

  for (const item of pathRules) {
    if (!item || typeof item !== 'object') continue;
    const policyId = String(item.policy_id ?? item.policyId ?? '').trim();
    const path = String(item.path ?? '').trim();
    if (!policyId || !path) continue;
    await runDslRows(
      runner,
      query()
        .input({
          policy_id: param('policy_id', policyId),
          path: param('path', path),
        })
        .put('om_perm_path_rule', ['policy_id', 'path'], [])
    );
  }
}

function _parsePermPathRule(rawPath) {
  const s = String(rawPath || '').trim();
  if (!s) return [];
  if (s.startsWith('[')) {
    try {
      const parsed = JSON.parse(s);
      if (Array.isArray(parsed)) {
        return parsed.map((x) => String(x || '').trim()).filter(Boolean);
      }
    } catch (_) {
    }
    return [];
  }
  const withoutLeading = s.startsWith('/') ? s.replace(/^\/+/, '') : s;
  return withoutLeading
    .split('/')
    .map((x) => String(x || '').trim())
    .filter(Boolean);
}

function _looksLikeJsonLiteral(s) {
  const t = String(s || '').trim();
  if (!t) return false;
  const head = t[0];
  if (head === '{' || head === '[' || head === '"') return true;
  if (t === 'true' || t === 'false' || t === 'null') return true;
  if (/^-?\d+(\.\d+)?([eE][+-]?\d+)?$/.test(t)) return true;
  return false;
}

function _parseLiteralValue(raw) {
  const s = String(raw == null ? '' : raw).trim();
  if (_looksLikeJsonLiteral(s)) {
    try {
      return JSON.parse(s);
    } catch (_) {
    }
  }
  return s;
}

function _evaluateAbacOp(op, leftValue, rightValue) {
  const o = String(op || '').trim();
  if (o === '==') return Object.is(leftValue, rightValue);
  if (o === '!=') return !Object.is(leftValue, rightValue);

  if (o === '>' || o === '>=' || o === '<' || o === '<=') {
    const a = leftValue;
    const b = rightValue;
    const bothNums = typeof a === 'number' && Number.isFinite(a) && typeof b === 'number' && Number.isFinite(b);
    const bothStr = typeof a === 'string' && typeof b === 'string';
    const x = bothNums || bothStr ? a : String(a);
    const y = bothNums || bothStr ? b : String(b);
    if (o === '>') return x > y;
    if (o === '>=') return x >= y;
    if (o === '<') return x < y;
    return x <= y;
  }

  throw new Error(`Unsupported ABAC op '${o}'`);
}

async function _getOutgoingNeighborsForPerm(runner, fromId, relName, asOf) {
  if (asOf) {
    const n = await getNeighborsAsOf(runner, fromId, relName, asOf);
    return Array.isArray(n.outgoing) ? n.outgoing : [];
  }
  const n = await getNeighbors(runner, fromId, relName, 'outgoing');
  return Array.isArray(n.outgoing) ? n.outgoing : [];
}

async function _findWitnessForRelPath(runner, subjectId, resourceId, relPath, asOf) {
  const path = Array.isArray(relPath) ? relPath.filter(Boolean) : [];
  if (!path.length) {
    return subjectId === resourceId ? [] : null;
  }

  let frontier = new Map();
  frontier.set(subjectId, []);

  for (const relName of path) {
    const canonicalRelName = await resolveRel(runner, relName);
    const next = new Map();

    const fromIds = [...frontier.keys()].sort((l, r) => l.localeCompare(r));
    for (const fromId of fromIds) {
      const witnessSoFar = frontier.get(fromId);
      const outgoing = await _getOutgoingNeighborsForPerm(runner, fromId, canonicalRelName, asOf);
      const sortedOutgoing = [...outgoing].sort((l, r) => String(l.entityId).localeCompare(String(r.entityId)));
      for (const nb of sortedOutgoing) {
        const toId = String(nb.entityId || '').trim();
        if (!toId) continue;
        const hop = { fromId, relName: canonicalRelName, toId };
        if (!next.has(toId)) {
          next.set(toId, [...witnessSoFar, hop]);
        }
      }
    }

    frontier = next;
    if (!frontier.size) return null;
  }

  return frontier.has(resourceId) ? frontier.get(resourceId) : null;
}

async function checkAccess(runner, input) {
  const payload = input && typeof input === 'object' ? input : {};
  const subjectId = String(payload.subjectId || '').trim();
  const action = String(payload.action || '').trim();
  const resourceId = String(payload.resourceId || '').trim();
  const hasAsOf = Object.prototype.hasOwnProperty.call(payload, 'asOf');
  const asOfRaw = hasAsOf ? String(payload.asOf || '').trim() : '';
  const asOf = asOfRaw ? _normalizeAsOfTimestamp(asOfRaw, 'asOf') : null;

  if (!subjectId) throw new Error('subjectId is required');
  if (!action) throw new Error('action is required');
  if (!resourceId) throw new Error('resourceId is required');

  const explanation = {
    input: { subjectId, action, resourceId, ...(asOf ? { asOf } : {}) },
    evaluatedPolicies: [],
    final: { allow: false, allowPolicies: [], denyPolicies: [] },
  };

  let policiesRows;
  try {
    policiesRows = await runDslRows(
      runner,
      query()
        .select(['policy_id', 'effect', 'action', 'resource_type', 'enabled', 'description'])
        .fromStored('om_perm_policy', {
          policy_id: dsl.var('policy_id'),
          effect: dsl.var('effect'),
          action: dsl.var('action'),
          resource_type: dsl.var('resource_type'),
          enabled: dsl.var('enabled'),
          description: dsl.var('description'),
        })
    );
  } catch (e) {
    if (_isStoredRelationMissingError(e)) {
      return { allow: false, matchedPolicies: [], explanation };
    }
    throw e;
  }

  let pathRows = [];
  let abacRows = [];
  try {
    pathRows = await runDslRows(
      runner,
      query()
        .select(['policy_id', 'path'])
        .fromStored('om_perm_path_rule', { policy_id: dsl.var('policy_id'), path: dsl.var('path') })
    );
  } catch (e) {
    if (!_isStoredRelationMissingError(e)) throw e;
  }
  try {
    abacRows = await runDslRows(
      runner,
      query()
        .select(['policy_id', 'left_ref', 'op', 'right_ref'])
        .fromStored('om_perm_abac_rule', {
          policy_id: dsl.var('policy_id'),
          left_ref: dsl.var('left_ref'),
          op: dsl.var('op'),
          right_ref: dsl.var('right_ref'),
        })
    );
  } catch (e) {
    if (!_isStoredRelationMissingError(e)) throw e;
  }

  const pathByPolicy = new Map();
  for (const [pid, p] of pathRows) {
    const policyId = String(pid || '').trim();
    const path = String(p || '').trim();
    if (!policyId || !path) continue;
    if (!pathByPolicy.has(policyId)) pathByPolicy.set(policyId, []);
    pathByPolicy.get(policyId).push(path);
  }
  for (const list of pathByPolicy.values()) {
    list.sort((l, r) => l.localeCompare(r));
  }

  const abacByPolicy = new Map();
  for (const [pid, leftRef, op, rightRef] of abacRows) {
    const policyId = String(pid || '').trim();
    if (!policyId) continue;
    const rule = {
      left_ref: String(leftRef || '').trim(),
      op: String(op || '').trim(),
      right_ref: String(rightRef || '').trim(),
    };
    if (!rule.left_ref || !rule.op || !rule.right_ref) continue;
    if (!abacByPolicy.has(policyId)) abacByPolicy.set(policyId, []);
    abacByPolicy.get(policyId).push(rule);
  }
  for (const list of abacByPolicy.values()) {
    list.sort((l, r) => {
      const a = `${l.left_ref}\u0001${l.op}\u0001${l.right_ref}`;
      const b = `${r.left_ref}\u0001${r.op}\u0001${r.right_ref}`;
      return a.localeCompare(b);
    });
  }

  const resourceType = await getEntityType(runner, resourceId);
  const subjectType = await getEntityType(runner, subjectId);

  const propCache = new Map();
  async function getProp(entityId, entityTypeName, attrName) {
    const key = `${entityId}\u0001${entityTypeName}\u0001${attrName}\u0001${asOf || ''}`;
    if (propCache.has(key)) return propCache.get(key);
    const canonicalAttr = await resolveAttr(runner, entityTypeName, attrName);
    const value = asOf
      ? await getPropertyAsOf(runner, entityId, canonicalAttr, asOf)
      : await getProperty(runner, entityId, canonicalAttr);
    propCache.set(key, value);
    return value;
  }

  async function resolveRef(ref) {
    const raw = String(ref || '').trim();
    if (!raw) return undefined;
    if (raw === 'subject.type') return subjectType;
    if (raw === 'resource.type') return resourceType;

    if (raw.startsWith('subject.')) {
      const attr = raw.slice('subject.'.length).trim();
      return getProp(subjectId, subjectType, attr);
    }
    if (raw.startsWith('resource.')) {
      const attr = raw.slice('resource.'.length).trim();
      return getProp(resourceId, resourceType, attr);
    }
    return _parseLiteralValue(raw);
  }

  const policies = policiesRows
    .map(([policyId, effect, polAction, resourceTypeRaw, enabled, description]) => ({
      policyId: String(policyId || '').trim(),
      effect: String(effect || '').trim().toLowerCase(),
      action: String(polAction || '').trim(),
      resourceType: resourceTypeRaw == null ? '' : String(resourceTypeRaw).trim(),
      enabled: !!enabled,
      description: description == null ? '' : String(description),
    }))
    .filter((p) => p.policyId)
    .sort((l, r) => l.policyId.localeCompare(r.policyId));

  const matchedPolicies = [];
  const fieldVisibility = {};

  for (const pol of policies) {
    const evalEntry = {
      policyId: pol.policyId,
      effect: pol.effect,
      action: pol.action,
      resourceType: pol.resourceType,
      enabled: pol.enabled,
      description: pol.description,
      matched: false,
      path: { rules: [], matched: false, matchedRule: null, witness: null },
      abac: { rules: [], matched: true },
    };

    if (!pol.enabled) {
      explanation.evaluatedPolicies.push(evalEntry);
      continue;
    }
    if (pol.action !== action) {
      explanation.evaluatedPolicies.push(evalEntry);
      continue;
    }
    if (pol.resourceType) {
      const canonicalPolicyType = await resolveType(runner, pol.resourceType);
      const ok = await isSubtypeOf(runner, resourceType, canonicalPolicyType);
      if (!ok) {
        explanation.evaluatedPolicies.push(evalEntry);
        continue;
      }
      evalEntry.resourceType = canonicalPolicyType;
    }

    const paths = pathByPolicy.get(pol.policyId) || [];
    evalEntry.path.rules = paths.slice();

    const parsedPaths = [];
    for (const rawPath of paths) {
      const rels = _parsePermPathRule(rawPath);
      if (!rels.length) continue;
      parsedPaths.push({
        raw: rawPath,
        rels,
        key: `${rels.length}\u0001${rels.join('/')}`,
      });
    }
    parsedPaths.sort((l, r) => l.key.localeCompare(r.key) || l.raw.localeCompare(r.raw));

    let witness = null;
    let matchedRule = null;
    for (const item of parsedPaths) {
      witness = await _findWitnessForRelPath(runner, subjectId, resourceId, item.rels, asOf);
      if (witness) {
        matchedRule = item.raw;
        break;
      }
    }
    evalEntry.path.matched = !!witness;
    evalEntry.path.matchedRule = matchedRule;
    evalEntry.path.witness = witness;
    if (!witness) {
      explanation.evaluatedPolicies.push(evalEntry);
      continue;
    }

    const abacRules = abacByPolicy.get(pol.policyId) || [];
    let abacOk = true;
    for (const rule of abacRules) {
      const leftRef = rule.left_ref;
      const op = rule.op;
      const rightRef = rule.right_ref;

      if (String(leftRef).startsWith('field.') && String(op).trim() === 'hide') {
        const field = String(leftRef).slice('field.'.length).trim();
        const value = await resolveRef(rightRef);
        const truthy = !!value;
        evalEntry.abac.rules.push({
          leftRef,
          op,
          rightRef,
          kind: 'hide',
          field,
          rightValue: value,
          result: truthy,
        });
        if (truthy && field) {
          fieldVisibility[field] = 'hidden';
        }
        continue;
      }

      let leftValue;
      let rightValue;
      let result = false;
      let error = null;
      try {
        leftValue = await resolveRef(leftRef);
        rightValue = await resolveRef(rightRef);
        result = _evaluateAbacOp(op, leftValue, rightValue);
      } catch (e) {
        abacOk = false;
        error = e && e.message ? String(e.message) : String(e);
      }

      evalEntry.abac.rules.push({ leftRef, op, rightRef, leftValue, rightValue, result, ...(error ? { error } : {}) });
      if (!result) abacOk = false;
    }
    evalEntry.abac.matched = abacOk;
    if (!abacOk) {
      explanation.evaluatedPolicies.push(evalEntry);
      continue;
    }

    evalEntry.matched = true;
    explanation.evaluatedPolicies.push(evalEntry);
    matchedPolicies.push({
      policyId: pol.policyId,
      effect: pol.effect,
      action: pol.action,
      resourceType: evalEntry.resourceType,
      description: pol.description,
      witness,
    });
  }

  matchedPolicies.sort((l, r) => String(l.policyId).localeCompare(String(r.policyId)));

  const deny = matchedPolicies.filter((p) => p.effect === 'deny').map((p) => p.policyId);
  const allow = matchedPolicies.filter((p) => p.effect === 'allow').map((p) => p.policyId);
  const finalAllow = deny.length ? false : allow.length > 0;

  explanation.final.allow = finalAllow;
  explanation.final.allowPolicies = allow;
  explanation.final.denyPolicies = deny;

  const out = {
    allow: finalAllow,
    matchedPolicies,
    explanation,
  };
  if (Object.keys(fieldVisibility).length) {
    out.fieldVisibility = fieldVisibility;
  }
  return out;
}

async function getSchemaState(runner) {
  const rows = await runDslRows(
    runner,
    query()
      .select(['current_version', 'current_checksum'])
      .fromStored('om_schema_state', {
        id: param('id', 'default'),
        current_version: dsl.var('current_version'),
        current_checksum: dsl.var('current_checksum'),
      })
      .limit(1)
  );
  if (!rows.length) {
    throw new Error("Schema state not initialized (missing om_schema_state row id='default'); call initSchema(runner) first");
  }
  const [currentVersion, checksum] = rows[0];
  return { currentVersion, checksum };
}

async function listSchemaVersions(runner) {
  const rows = await runDslRows(
    runner,
    query()
      .select(['version', 'created_at', 'label', 'description', 'parent_version', 'checksum'])
      .fromStored('om_schema_version', {
        version: dsl.var('version'),
        created_at: dsl.var('created_at'),
        label: dsl.var('label'),
        description: dsl.var('description'),
        parent_version: dsl.var('parent_version'),
        checksum: dsl.var('checksum'),
      })
      .order('version')
  );
  return rows.map(([version, createdAt, label, description, parentVersion, checksum]) => ({
    version,
    createdAt,
    label,
    description,
    parentVersion: parentVersion === '' ? null : parentVersion,
    checksum,
  }));
}

function _normalizeMigrationSpec(spec) {
  const s = spec && typeof spec === 'object' ? spec : {};
  const migrationId = String(s.migrationId || s.migration_id || '').trim();
  const fromVersionRaw = s.fromVersion ?? s.from_version;
  const toVersionRaw = s.toVersion ?? s.to_version;
  const fromVersion = Number(fromVersionRaw);
  const toVersion = Number(toVersionRaw);
  const strict = s.strict !== false;
  const label = Object.prototype.hasOwnProperty.call(s, 'label') ? String(s.label || '') : '';
  const description = Object.prototype.hasOwnProperty.call(s, 'description') ? String(s.description || '') : '';
  const steps = Array.isArray(s.steps) ? s.steps : [];
  return { migrationId, fromVersion, toVersion, strict, label, description, steps };
}

function _normalizeStepKind(step) {
  if (!step || typeof step !== 'object') return '';
  const k = String(step.kind || step.type || '').trim();
  return k;
}

function _valueTypeMatches(value, targetValueType) {
  if (targetValueType === 'String') return typeof value === 'string';
  if (targetValueType === 'Number') return typeof value === 'number' && Number.isFinite(value);
  if (targetValueType === 'Bool') return typeof value === 'boolean';
  if (targetValueType === 'Json') return value !== null && typeof value === 'object';
  if (targetValueType === 'Validity') {
    // Stored Validity values are objects (cozo-lib-bun uses validity(...) values).
    return value !== null && typeof value === 'object';
  }
  return false;
}

function _canCoerceValueToType(value, targetValueType) {
  if (_valueTypeMatches(value, targetValueType)) return true;
  if (targetValueType === 'Number' && typeof value === 'string') {
    const n = Number(value);
    return Number.isFinite(n);
  }
  if (targetValueType === 'Bool' && typeof value === 'string') {
    const s = value.trim().toLowerCase();
    return s === 'true' || s === 'false';
  }
  return false;
}

async function _preflightAttributeValueTypeChange(runner, canonicalTypeName, canonicalAttrName, nextValueType) {
  const rows = await runRows(
    runner,
    `
?[entity_id, value] :=
  *om_entity{ id: entity_id, type_name: $type_name, label: _label },
  *om_property{ entity_id, attr_name: $attr_name, value @ "NOW" }
    `.trim(),
    { type_name: canonicalTypeName, attr_name: canonicalAttrName }
  );
  const bad = [];
  for (const [entityId, value] of rows) {
    if (!_canCoerceValueToType(value, nextValueType)) {
      bad.push({ entityId, value });
      if (bad.length >= 10) break;
    }
  }
  if (bad.length) {
    const samples = bad
      .map((x) => `${String(x.entityId)}=${typeof x.value === 'string' ? JSON.stringify(x.value) : String(x.value)}`)
      .join(', ');
    throw new Error(
      `Incompatible valueType change for ${canonicalTypeName}.${canonicalAttrName}: cannot convert existing values to ${nextValueType} (samples: ${samples})`
    );
  }
}

async function _readSchemaSnapshotParts(runner) {
  const types = await runDslRows(
    runner,
    query()
      .select(['name', 'description', 'parent_type'])
      .fromStored('om_type', {
        name: dsl.var('name'),
        description: dsl.var('description'),
        parent_type: dsl.var('parent_type'),
      })
      .order('name')
  );

  const mixins = await runDslRows(
    runner,
    query()
      .select(['name', 'description'])
      .fromStored('om_mixin', {
        name: dsl.var('name'),
        description: dsl.var('description'),
      })
      .order('name')
  );

  const typeMixins = await runDslRows(
    runner,
    query()
      .select(['type_name', 'mixin_name'])
      .fromStored('om_type_mixin', {
        type_name: dsl.var('type_name'),
        mixin_name: dsl.var('mixin_name'),
      })
      .order('type_name', 'mixin_name')
  );

  const attrDefs = await runDslRows(
    runner,
    query()
      .select(['type_name', 'attr_name', 'value_type', 'required'])
      .fromStored('om_attr_def', {
        type_name: dsl.var('type_name'),
        attr_name: dsl.var('attr_name'),
        value_type: dsl.var('value_type'),
        required: dsl.var('required'),
      })
      .order('type_name', 'attr_name')
  );

  const relDefs = await runDslRows(
    runner,
    query()
      .select(['rel_name', 'from_type', 'to_type', 'directed'])
      .fromStored('om_rel_def', {
        rel_name: dsl.var('rel_name'),
        from_type: dsl.var('from_type'),
        to_type: dsl.var('to_type'),
        directed: dsl.var('directed'),
      })
      .order('rel_name')
  );

  const aliasTypes = await runDslRows(
    runner,
    query()
      .select(['alias', 'canonical'])
      .fromStored('om_alias_type', {
        alias: dsl.var('alias'),
        canonical: dsl.var('canonical'),
      })
      .order('alias')
  ).catch((e) => {
    if (_isStoredRelationMissingError(e)) return [];
    throw e;
  });

  const aliasRels = await runDslRows(
    runner,
    query()
      .select(['alias', 'canonical'])
      .fromStored('om_alias_rel', {
        alias: dsl.var('alias'),
        canonical: dsl.var('canonical'),
      })
      .order('alias')
  ).catch((e) => {
    if (_isStoredRelationMissingError(e)) return [];
    throw e;
  });

  const aliasAttrs = await runDslRows(
    runner,
    query()
      .select(['type_name', 'alias_attr', 'canonical_attr'])
      .fromStored('om_alias_attr', {
        type_name: dsl.var('type_name'),
        alias_attr: dsl.var('alias_attr'),
        canonical_attr: dsl.var('canonical_attr'),
      })
      .order('type_name', 'alias_attr')
  ).catch((e) => {
    if (_isStoredRelationMissingError(e)) return [];
    throw e;
  });

  const existentialRules = await runDslRows(
    runner,
    query()
      .select(['rule_name', 'spec_json', 'mode', 'message', 'enabled'])
      .fromStored('om_existential_rule_def', {
        rule_name: dsl.var('rule_name'),
        spec_json: dsl.var('spec_json'),
        mode: dsl.var('mode'),
        message: dsl.var('message'),
        enabled: dsl.var('enabled'),
      })
      .order('rule_name')
  ).catch((e) => {
    if (_isStoredRelationMissingError(e)) return [];
    throw e;
  });

  return {
    types,
    mixins,
    typeMixins,
    attrDefs,
    relDefs,
    aliasTypes,
    aliasRels,
    aliasAttrs,
    existentialRules,
  };
}

async function _readPermSnapshotParts(runner) {
  // Optional permission policy metadata snapshot.
  // Must gracefully handle missing relations (older schemas / deployments).
  const out = {
    exists: true,
    actions: [],
    policies: [],
    abacRules: [],
    pathRules: [],
  };

  let anyRelationExists = false;

  const actions = await runDslRows(
    runner,
    query()
      .select(['action', 'description'])
      .fromStored('om_perm_action', {
        action: dsl.var('action'),
        description: dsl.var('description'),
      })
      .order('action')
  ).catch((e) => {
    if (_isStoredRelationMissingError(e)) return null;
    throw e;
  });
  if (actions) {
    anyRelationExists = true;
    out.actions = actions;
  }

  const policies = await runDslRows(
    runner,
    query()
      .select(['policy_id', 'effect', 'action', 'resource_type', 'enabled', 'description'])
      .fromStored('om_perm_policy', {
        policy_id: dsl.var('policy_id'),
        effect: dsl.var('effect'),
        action: dsl.var('action'),
        resource_type: dsl.var('resource_type'),
        enabled: dsl.var('enabled'),
        description: dsl.var('description'),
      })
      .order('policy_id')
  ).catch((e) => {
    if (_isStoredRelationMissingError(e)) return null;
    throw e;
  });
  if (policies) {
    anyRelationExists = true;
    out.policies = policies;
  }

  const abacRules = await runDslRows(
    runner,
    query()
      .select(['policy_id', 'left_ref', 'op', 'right_ref'])
      .fromStored('om_perm_abac_rule', {
        policy_id: dsl.var('policy_id'),
        left_ref: dsl.var('left_ref'),
        op: dsl.var('op'),
        right_ref: dsl.var('right_ref'),
      })
      .order('policy_id', 'left_ref', 'op', 'right_ref')
  ).catch((e) => {
    if (_isStoredRelationMissingError(e)) return null;
    throw e;
  });
  if (abacRules) {
    anyRelationExists = true;
    out.abacRules = abacRules;
  }

  const pathRules = await runDslRows(
    runner,
    query()
      .select(['policy_id', 'path'])
      .fromStored('om_perm_path_rule', {
        policy_id: dsl.var('policy_id'),
        path: dsl.var('path'),
      })
      .order('policy_id', 'path')
  ).catch((e) => {
    if (_isStoredRelationMissingError(e)) return null;
    throw e;
  });
  if (pathRules) {
    anyRelationExists = true;
    out.pathRules = pathRules;
  }

  return anyRelationExists ? out : null;
}

function _computeHierarchyFromTypeRows(typeRows) {
  const rows = Array.isArray(typeRows) ? typeRows : [];
  const parentByType = {};
  const childrenByType = {};
  const all = new Set();

  for (const r of rows) {
    if (!Array.isArray(r) || !r.length) continue;
    const name = String(r[0] || '').trim();
    if (!name) continue;
    all.add(name);
    const parent = r[2];
    const pt = (parent === null || parent === '' || parent === undefined) ? null : String(parent);
    parentByType[name] = pt;
  }

  for (const name of all) {
    if (!childrenByType[name]) childrenByType[name] = [];
  }

  for (const [name, pt] of Object.entries(parentByType)) {
    if (pt && all.has(pt)) {
      if (!childrenByType[pt]) childrenByType[pt] = [];
      childrenByType[pt].push(name);
    }
  }

  const roots = [];
  for (const name of all) {
    const pt = Object.prototype.hasOwnProperty.call(parentByType, name) ? parentByType[name] : null;
    if (!pt || !all.has(pt)) roots.push(name);
  }
  roots.sort((l, r) => l.localeCompare(r));

  for (const k of Object.keys(childrenByType)) {
    childrenByType[k].sort((l, r) => l.localeCompare(r));
  }

  // Deterministic key ordering is handled by JSON stringify in diff.
  return { roots, parentByType, childrenByType };
}

function _composeSchemaSnapshot(version, createdAt, label, description, parts, perm) {
  const schema = {
    // Keep the original P1 snapshot layout (arrays-of-arrays) for compatibility.
    types: parts.types,
    mixins: parts.mixins,
    typeMixins: parts.typeMixins,
    attrDefs: parts.attrDefs,
    relDefs: parts.relDefs,
    aliasTypes: parts.aliasTypes,
    aliasRels: parts.aliasRels,
    aliasAttrs: parts.aliasAttrs,

    // Explicit relation names required by P2/WAVE-P2-02.
    om_type: parts.types,
    om_mixin: parts.mixins,
    om_type_mixin: parts.typeMixins,
    om_attr_def: parts.attrDefs,
    om_rel_def: parts.relDefs,
    om_alias_type: parts.aliasTypes,
    om_alias_rel: parts.aliasRels,
    om_alias_attr: parts.aliasAttrs,
    om_existential_rule_def: parts.existentialRules || [],
  };

  const permSection = perm || { exists: false, actions: [], policies: [], abacRules: [], pathRules: [] };

  const snapshot = {
    version,
    createdAt,
    schema,

    // Convenience sections expected by existing tests.
    types: parts.types,
    attrs: parts.attrDefs,
    rels: parts.relDefs,
    hierarchy: _computeHierarchyFromTypeRows(parts.types),
    alias: {
      types: parts.aliasTypes,
      rels: parts.aliasRels,
      attrs: parts.aliasAttrs,
    },
    perm: permSection,
  };

  if (label !== undefined) snapshot.label = label;
  if (description !== undefined) snapshot.description = description;
  return snapshot;
}

function _stableNormalizeForJson(value) {
  if (Array.isArray(value)) return value.map(_stableNormalizeForJson);
  if (!value || typeof value !== 'object') return value;
  const keys = Object.keys(value).sort((l, r) => l.localeCompare(r));
  const out = {};
  for (const k of keys) {
    out[k] = _stableNormalizeForJson(value[k]);
  }
  return out;
}

function _stableStringify(value) {
  return JSON.stringify(_stableNormalizeForJson(value));
}

function _diffByKey(fromItems, toItems, keyFields) {
  const fromList = Array.isArray(fromItems) ? fromItems : [];
  const toList = Array.isArray(toItems) ? toItems : [];
  const keys = Array.isArray(keyFields) ? keyFields : [];
  if (!keys.length) throw new Error('diff requires non-empty keyFields');

  const keyString = (obj) => keys.map((k) => String(obj[k] ?? '')).join('\u0001');

  const fromMap = new Map();
  for (const it of fromList) {
    if (!it || typeof it !== 'object') continue;
    fromMap.set(keyString(it), it);
  }
  const toMap = new Map();
  for (const it of toList) {
    if (!it || typeof it !== 'object') continue;
    toMap.set(keyString(it), it);
  }

  const allKeys = new Set([...fromMap.keys(), ...toMap.keys()]);
  const sortedKeys = [...allKeys].sort((l, r) => l.localeCompare(r));

  const added = [];
  const removed = [];
  const updated = [];

  for (const k of sortedKeys) {
    const a = fromMap.get(k);
    const b = toMap.get(k);
    if (a && !b) {
      removed.push(a);
      continue;
    }
    if (!a && b) {
      added.push(b);
      continue;
    }
    if (!a || !b) continue;
    if (_stableStringify(a) !== _stableStringify(b)) {
      const key = {};
      for (const f of keys) key[f] = b[f];
      updated.push({ key, from: a, to: b });
    }
  }

  return { added, removed, updated };
}

function _rowsToObjects(rows, cols) {
  const list = Array.isArray(rows) ? rows : [];
  const columns = Array.isArray(cols) ? cols : [];
  return list
    .filter((r) => Array.isArray(r))
    .map((r) => {
      const obj = {};
      for (let i = 0; i < columns.length; i++) {
        obj[columns[i]] = r[i];
      }
      return obj;
    });
}

async function readSchemaSnapshot(runner, version) {
  const v = Number(version);
  if (!Number.isFinite(v) || v <= 0) throw new Error('version must be a positive number');

  const rows = await runDslRows(
    runner,
    query()
      .select(['snapshot_json'])
      .fromStored('om_schema_snapshot', {
        version: param('version', v),
        snapshot_json: dsl.var('snapshot_json'),
      })
      .limit(1)
  ).catch((e) => {
    if (_isStoredRelationMissingError(e)) {
      throw new Error("Schema snapshots not initialized (missing om_schema_snapshot); call initSchema(runner) first");
    }
    throw e;
  });

  if (!rows.length) return null;
  const raw = rows[0][0];
  if (raw == null) return null;
  if (typeof raw === 'string') {
    const s = String(raw);
    if (!s.trim()) return null;
    try {
      return JSON.parse(s);
    } catch (e) {
      throw new Error(`Invalid snapshot_json for version ${v}: expected JSON string`);
    }
  }
  if (typeof raw === 'object') return raw;
  throw new Error(`Invalid snapshot_json for version ${v}: expected JSON object or string`);
}

async function writeSchemaSnapshot(runner, version, options) {
  const v = Number(version);
  if (!Number.isFinite(v) || v <= 0) throw new Error('version must be a positive number');

  const opts = options && typeof options === 'object' ? options : {};
  const ensureCurrent = opts.ensureCurrent !== false;
  const state = await getSchemaState(runner);
  if (ensureCurrent && Number(state.currentVersion) !== v) {
    throw new Error(
      `Refusing to write snapshot for version=${v} because currentVersion=${state.currentVersion}. ` +
      `Pass options.ensureCurrent=false to override.`
    );
  }

  const now = opts.createdAt ? String(opts.createdAt) : new Date().toISOString();
  const label = Object.prototype.hasOwnProperty.call(opts, 'label') ? String(opts.label || '') : undefined;
  const description = Object.prototype.hasOwnProperty.call(opts, 'description') ? String(opts.description || '') : undefined;

  const parts = await _readSchemaSnapshotParts(runner);
  const perm = await _readPermSnapshotParts(runner);
  const snapshot = _composeSchemaSnapshot(v, now, label, description, parts, perm);

  await runDslRows(
    runner,
    query()
      .input({
        version: param('version', v),
        snapshot_json: param('snapshot_json', snapshot),
      })
      .put('om_schema_snapshot', ['version'], ['snapshot_json'])
  ).catch((e) => {
    if (_isStoredRelationMissingError(e)) {
      throw new Error("Schema snapshots not initialized (missing om_schema_snapshot); call initSchema(runner) first");
    }
    throw e;
  });

  return snapshot;
}

function _diffSchemaSnapshots(fromSnapshot, toSnapshot) {
  if (!fromSnapshot || typeof fromSnapshot !== 'object') throw new Error('fromSnapshot must be an object');
  if (!toSnapshot || typeof toSnapshot !== 'object') throw new Error('toSnapshot must be an object');

  const fromSchema = (fromSnapshot.schema && typeof fromSnapshot.schema === 'object') ? fromSnapshot.schema : {};
  const toSchema = (toSnapshot.schema && typeof toSnapshot.schema === 'object') ? toSnapshot.schema : {};

  // Support both v1 snapshot shape and newer explicit table names.
  const pickRows = (snap, schemaObj, tableKey, legacyKey, rootKey) => {
    if (schemaObj && Object.prototype.hasOwnProperty.call(schemaObj, tableKey)) return schemaObj[tableKey];
    if (schemaObj && legacyKey && Object.prototype.hasOwnProperty.call(schemaObj, legacyKey)) return schemaObj[legacyKey];
    if (snap && rootKey && Object.prototype.hasOwnProperty.call(snap, rootKey)) return snap[rootKey];
    return [];
  };

  const fromPerm = (fromSnapshot.perm && typeof fromSnapshot.perm === 'object') ? fromSnapshot.perm : null;
  const toPerm = (toSnapshot.perm && typeof toSnapshot.perm === 'object') ? toSnapshot.perm : null;

  const diff = {
    fromVersion: Number(fromSnapshot.version) || null,
    toVersion: Number(toSnapshot.version) || null,
    schema: {
      om_type: _diffByKey(
        _rowsToObjects(pickRows(fromSnapshot, fromSchema, 'om_type', 'types', 'types'), ['name', 'description', 'parent_type']),
        _rowsToObjects(pickRows(toSnapshot, toSchema, 'om_type', 'types', 'types'), ['name', 'description', 'parent_type']),
        ['name']
      ),
      om_mixin: _diffByKey(
        _rowsToObjects(pickRows(fromSnapshot, fromSchema, 'om_mixin', 'mixins', null), ['name', 'description']),
        _rowsToObjects(pickRows(toSnapshot, toSchema, 'om_mixin', 'mixins', null), ['name', 'description']),
        ['name']
      ),
      om_type_mixin: _diffByKey(
        _rowsToObjects(pickRows(fromSnapshot, fromSchema, 'om_type_mixin', 'typeMixins', null), ['type_name', 'mixin_name']),
        _rowsToObjects(pickRows(toSnapshot, toSchema, 'om_type_mixin', 'typeMixins', null), ['type_name', 'mixin_name']),
        ['type_name', 'mixin_name']
      ),
      om_attr_def: _diffByKey(
        _rowsToObjects(pickRows(fromSnapshot, fromSchema, 'om_attr_def', 'attrDefs', 'attrs'), ['type_name', 'attr_name', 'value_type', 'required']),
        _rowsToObjects(pickRows(toSnapshot, toSchema, 'om_attr_def', 'attrDefs', 'attrs'), ['type_name', 'attr_name', 'value_type', 'required']),
        ['type_name', 'attr_name']
      ),
      om_rel_def: _diffByKey(
        _rowsToObjects(pickRows(fromSnapshot, fromSchema, 'om_rel_def', 'relDefs', 'rels'), ['rel_name', 'from_type', 'to_type', 'directed']),
        _rowsToObjects(pickRows(toSnapshot, toSchema, 'om_rel_def', 'relDefs', 'rels'), ['rel_name', 'from_type', 'to_type', 'directed']),
        ['rel_name']
      ),
      om_existential_rule_def: _diffByKey(
        _rowsToObjects(pickRows(fromSnapshot, fromSchema, 'om_existential_rule_def', 'existentialRules', null), ['rule_name', 'spec_json', 'mode', 'message', 'enabled']),
        _rowsToObjects(pickRows(toSnapshot, toSchema, 'om_existential_rule_def', 'existentialRules', null), ['rule_name', 'spec_json', 'mode', 'message', 'enabled']),
        ['rule_name']
      ),
    },
    aliases: {
      om_alias_type: _diffByKey(
        _rowsToObjects(pickRows(fromSnapshot, fromSchema, 'om_alias_type', 'aliasTypes', null), ['alias', 'canonical']),
        _rowsToObjects(pickRows(toSnapshot, toSchema, 'om_alias_type', 'aliasTypes', null), ['alias', 'canonical']),
        ['alias']
      ),
      om_alias_rel: _diffByKey(
        _rowsToObjects(pickRows(fromSnapshot, fromSchema, 'om_alias_rel', 'aliasRels', null), ['alias', 'canonical']),
        _rowsToObjects(pickRows(toSnapshot, toSchema, 'om_alias_rel', 'aliasRels', null), ['alias', 'canonical']),
        ['alias']
      ),
      om_alias_attr: _diffByKey(
        _rowsToObjects(pickRows(fromSnapshot, fromSchema, 'om_alias_attr', 'aliasAttrs', null), ['type_name', 'alias_attr', 'canonical_attr']),
        _rowsToObjects(pickRows(toSnapshot, toSchema, 'om_alias_attr', 'aliasAttrs', null), ['type_name', 'alias_attr', 'canonical_attr']),
        ['type_name', 'alias_attr']
      ),
    },
  };

  if (fromPerm || toPerm) {
    const fromP = fromPerm || { actions: [], policies: [], abacRules: [], pathRules: [] };
    const toP = toPerm || { actions: [], policies: [], abacRules: [], pathRules: [] };
    diff.perm = {
      om_perm_action: _diffByKey(
        _rowsToObjects(fromP.actions, ['action', 'description']),
        _rowsToObjects(toP.actions, ['action', 'description']),
        ['action']
      ),
      om_perm_policy: _diffByKey(
        _rowsToObjects(fromP.policies, ['policy_id', 'effect', 'action', 'resource_type', 'enabled', 'description']),
        _rowsToObjects(toP.policies, ['policy_id', 'effect', 'action', 'resource_type', 'enabled', 'description']),
        ['policy_id']
      ),
      om_perm_abac_rule: _diffByKey(
        _rowsToObjects(fromP.abacRules, ['policy_id', 'left_ref', 'op', 'right_ref']),
        _rowsToObjects(toP.abacRules, ['policy_id', 'left_ref', 'op', 'right_ref']),
        ['policy_id', 'left_ref', 'op', 'right_ref']
      ),
      om_perm_path_rule: _diffByKey(
        _rowsToObjects(fromP.pathRules, ['policy_id', 'path']),
        _rowsToObjects(toP.pathRules, ['policy_id', 'path']),
        ['policy_id', 'path']
      ),
    };
  }

  return diff;
}

async function diffSchemaVersions(runner, fromVersion, toVersion) {
  const fv = Number(fromVersion);
  const tv = Number(toVersion);
  if (!Number.isFinite(fv) || fv <= 0) throw new Error('fromVersion must be a positive number');
  if (!Number.isFinite(tv) || tv <= 0) throw new Error('toVersion must be a positive number');

  let fromSnap = await readSchemaSnapshot(runner, fv);
  let toSnap = await readSchemaSnapshot(runner, tv);

  // If either snapshot is missing, we can only materialize it if it refers to the current schema state.
  if (!fromSnap || !toSnap) {
    const state = await getSchemaState(runner);
    const currentVersion = Number(state.currentVersion);
    if (!fromSnap) {
      if (fv === currentVersion) {
        fromSnap = await writeSchemaSnapshot(runner, fv, { ensureCurrent: true });
      } else {
        throw new Error(
          `Missing schema snapshot for version=${fv}. ` +
          `Can only auto-write snapshot for currentVersion=${state.currentVersion}.`
        );
      }
    }
    if (!toSnap) {
      if (tv === currentVersion) {
        toSnap = await writeSchemaSnapshot(runner, tv, { ensureCurrent: true });
      } else {
        throw new Error(
          `Missing schema snapshot for version=${tv}. ` +
          `Can only auto-write snapshot for currentVersion=${state.currentVersion}.`
        );
      }
    }
  }

  if (!fromSnap) throw new Error(`Missing schema snapshot for version=${fv}`);
  if (!toSnap) throw new Error(`Missing schema snapshot for version=${tv}`);

  return _diffSchemaSnapshots(fromSnap, toSnap);
}

function _computeSchemaChecksum(snapshotJsonValue) {
  const s = typeof snapshotJsonValue === 'string' ? snapshotJsonValue : JSON.stringify(snapshotJsonValue);
  return createHash('sha256').update(String(s || ''), 'utf8').digest('hex');
}

async function applySchemaMigration(runner, spec) {
  const { migrationId, fromVersion, toVersion, strict, label, description, steps } = _normalizeMigrationSpec(spec);
  if (!migrationId) throw new Error('migrationId is required');
  if (!Number.isFinite(fromVersion) || fromVersion <= 0) throw new Error('fromVersion must be a positive number');
  if (!Number.isFinite(toVersion) || toVersion <= 0) throw new Error('toVersion must be a positive number');
  if (toVersion === fromVersion) throw new Error('toVersion must be different from fromVersion');

  // Validate current schema state before entering write transaction.
  const state = await getSchemaState(runner);
  if (Number(state.currentVersion) !== fromVersion) {
    throw new Error(`Schema currentVersion=${state.currentVersion} does not match fromVersion=${fromVersion}`);
  }

  return _withWriteTxIfPossible(runner, async (txRunner) => {
    const now = new Date().toISOString();

    // Ensure a snapshot exists for fromVersion (materialize current schema state before applying steps).
    // This enables diffSchemaVersions(fromVersion, toVersion) to work deterministically.
    const fromSnapRows = await runDslRows(
      txRunner,
      query()
        .select(['snapshot_json'])
        .fromStored('om_schema_snapshot', {
          version: param('version', fromVersion),
          snapshot_json: dsl.var('snapshot_json'),
        })
        .limit(1)
    );
    if (!fromSnapRows.length) {
      const vRows = await runDslRows(
        txRunner,
        query()
          .select(['created_at', 'label', 'description'])
          .fromStored('om_schema_version', {
            version: param('version', fromVersion),
            created_at: dsl.var('created_at'),
            label: dsl.var('label'),
            description: dsl.var('description'),
            parent_version: dsl.var('_parent'),
            checksum: dsl.var('_checksum'),
          })
          .limit(1)
      ).catch((e) => {
        if (_isStoredRelationMissingError(e)) return [];
        throw e;
      });
      const createdAt0 = vRows.length ? String(vRows[0][0] || '').trim() : now;
      const label0 = vRows.length ? String(vRows[0][1] || '') : undefined;
      const description0 = vRows.length ? String(vRows[0][2] || '') : undefined;
      const fromParts = await _readSchemaSnapshotParts(txRunner);
      const fromPerm = await _readPermSnapshotParts(txRunner);
      const fromSnapshotJson = _composeSchemaSnapshot(
        fromVersion,
        createdAt0 || now,
        label0,
        description0,
        fromParts,
        fromPerm
      );
      await runDslRows(
        txRunner,
        query()
          .input({
            version: param('version', fromVersion),
            snapshot_json: param('snapshot_json', fromSnapshotJson),
          })
          .put('om_schema_snapshot', ['version'], ['snapshot_json'])
      );
    }

    // Apply steps.
    for (const step of steps) {
      const kind = _normalizeStepKind(step);
      if (!kind) throw new Error('Migration step kind is required');

      if (kind === 'addType') {
        const typeName = String(step.typeName || step.type_name || '').trim();
        if (!typeName) throw new Error('addType.typeName is required');
        const desc = Object.prototype.hasOwnProperty.call(step, 'description') ? String(step.description || '') : '';
        await defineType(txRunner, typeName, desc || typeName);
        continue;
      }

      if (kind === 'addAttribute') {
        const typeName = String(step.typeName || step.type_name || '').trim();
        const attrName = String(step.attrName || step.attr_name || '').trim();
        const valueType = String(step.valueType || step.value_type || '').trim();
        const required = step.required === true;
        if (!typeName) throw new Error('addAttribute.typeName is required');
        if (!attrName) throw new Error('addAttribute.attrName is required');
        if (!valueType) throw new Error('addAttribute.valueType is required');
        await defineAttribute(txRunner, typeName, attrName, valueType, required);
        continue;
      }

      if (kind === 'renameAttribute') {
        const typeNameRaw = String(step.typeName || step.type_name || '').trim();
        const fromAttrRaw = String(step.fromAttr || step.from_attr || '').trim();
        const toAttrRaw = String(step.toAttr || step.to_attr || '').trim();
        if (!typeNameRaw) throw new Error('renameAttribute.typeName is required');
        if (!fromAttrRaw) throw new Error('renameAttribute.fromAttr is required');
        if (!toAttrRaw) throw new Error('renameAttribute.toAttr is required');

        const typeName = await resolveType(txRunner, typeNameRaw);
        await _preloadAttrAliasesForType(txRunner, typeName);
        const fromAttr = await _resolveAttrForCanonicalType(txRunner, typeName, fromAttrRaw);
        const toAttr = await _resolveAttrForCanonicalType(txRunner, typeName, toAttrRaw);

        if (fromAttr === toAttr) continue;

        // Copy attr def to the new canonical name, then mark old as alias.
        const defRows = await runDslRows(
          txRunner,
          query()
            .select(['value_type', 'required'])
            .fromStored('om_attr_def', {
              type_name: param('type_name', typeName),
              attr_name: param('attr_name', fromAttr),
              value_type: dsl.var('value_type'),
              required: dsl.var('required'),
            })
            .limit(1)
        );
        if (!defRows.length) {
          throw new Error(`Cannot rename missing attribute '${typeName}.${fromAttr}'`);
        }
        const [valueType, required] = defRows[0];
        await defineAttribute(txRunner, typeName, toAttr, String(valueType), !!required);

        await runDslRows(
          txRunner,
          query()
            .input({
              type_name: param('type_name', typeName),
              alias_attr: param('alias_attr', fromAttr),
              canonical_attr: param('canonical_attr', toAttr),
            })
            .put('om_alias_attr', ['type_name', 'alias_attr'], ['canonical_attr'])
        );

        // Remove the old definition to avoid duplicated schema defs.
        await runDslRows(
          txRunner,
          query()
            .input({
              type_name: param('type_name', typeName),
              attr_name: param('attr_name', fromAttr),
            })
            .rm('om_attr_def', ['type_name', 'attr_name'])
        );
        await runDslRows(
          txRunner,
          query()
            .input({
              type_name: param('type_name', typeName),
              attr_name: param('attr_name', fromAttr),
            })
            .rm('om_attr_desc', ['type_name', 'attr_name'])
        ).catch((e) => {
          if (_isStoredRelationMissingError(e)) return;
          throw e;
        });
        continue;
      }

      if (kind === 'changeAttribute') {
        const typeNameRaw = String(step.typeName || step.type_name || '').trim();
        const attrNameRaw = String(step.attrName || step.attr_name || '').trim();
        const nextValueType = String(step.valueType || step.value_type || '').trim();
        const hasRequired = Object.prototype.hasOwnProperty.call(step, 'required');
        const nextRequired = step.required === true;
        if (!typeNameRaw) throw new Error('changeAttribute.typeName is required');
        if (!attrNameRaw) throw new Error('changeAttribute.attrName is required');
        if (!nextValueType) throw new Error('changeAttribute.valueType is required');

        const typeName = await resolveType(txRunner, typeNameRaw);
        await _preloadAttrAliasesForType(txRunner, typeName);
        const attrName = await _resolveAttrForCanonicalType(txRunner, typeName, attrNameRaw);

        if (strict) {
          await _preflightAttributeValueTypeChange(txRunner, typeName, attrName, nextValueType);
        }

        // Update schema definition.
        const currentDefRows = await runDslRows(
          txRunner,
          query()
            .select(['required'])
            .fromStored('om_attr_def', {
              type_name: param('type_name', typeName),
              attr_name: param('attr_name', attrName),
              value_type: dsl.var('_vt'),
              required: dsl.var('required'),
            })
            .limit(1)
        );
        if (!currentDefRows.length) {
          throw new Error(`Cannot change missing attribute '${typeName}.${attrName}'`);
        }
        const [required0] = currentDefRows[0];
        await defineAttribute(txRunner, typeName, attrName, nextValueType, hasRequired ? nextRequired : !!required0);
        continue;
      }

      throw new Error(`Unsupported migration step kind '${kind}'`);
    }

    // Snapshot + checksum for toVersion.
    const parts = await _readSchemaSnapshotParts(txRunner);
    const perm = await _readPermSnapshotParts(txRunner);
    const snapshotJson = _composeSchemaSnapshot(
      toVersion,
      now,
      label,
      description,
      parts,
      perm
    );
    const checksum = _computeSchemaChecksum(snapshotJson);

    // Ensure schema version row exists for toVersion.
    const existingRows = await runDslRows(
      txRunner,
      query()
        .select(['created_at'])
        .fromStored('om_schema_version', {
          version: param('version', toVersion),
          created_at: dsl.var('created_at'),
          label: dsl.var('_label'),
          description: dsl.var('_description'),
          parent_version: dsl.var('_parent'),
          checksum: dsl.var('_checksum'),
        })
        .limit(1)
    );
    const createdAt = existingRows.length ? String(existingRows[0][0] || '').trim() : now;
    await runDslRows(
      txRunner,
      query()
        .input({
          version: param('version', toVersion),
          created_at: param('created_at', createdAt || now),
          label: param('label', label || `v${toVersion}`),
          description: param('description', description || ''),
          parent_version: param('parent_version', fromVersion),
          checksum: param('checksum', checksum),
        })
        .put('om_schema_version', ['version'], ['created_at', 'label', 'description', 'parent_version', 'checksum'])
    );

    // Write snapshot.
    await runDslRows(
      txRunner,
      query()
        .input({
          version: param('version', toVersion),
          snapshot_json: param('snapshot_json', snapshotJson),
        })
        .put('om_schema_snapshot', ['version'], ['snapshot_json'])
    );

    // Update schema state.
    await runDslRows(
      txRunner,
      query()
        .input({
          id: param('id', 'default'),
          current_version: param('current_version', toVersion),
          current_checksum: param('current_checksum', checksum),
        })
        .put('om_schema_state', ['id'], ['current_version', 'current_checksum'])
    );

    // Migration log (written last; part of the same transaction).
    await runDslRows(
      txRunner,
      query()
        .input({
          migration_id: param('migration_id', migrationId),
          from_version: param('from_version', fromVersion),
          to_version: param('to_version', toVersion),
          applied_at: param('applied_at', now),
          applied_by: param('applied_by', ''),
          status: param('status', 'applied'),
          error: param('error', ''),
          summary_json: param('summary_json', {
            migrationId,
            fromVersion,
            toVersion,
            strict,
            stepsApplied: steps.length,
          }),
        })
        .put(
          'om_schema_migration',
          ['migration_id'],
          ['from_version', 'to_version', 'applied_at', 'applied_by', 'status', 'error', 'summary_json']
        )
    );
  });
}

function _getSnapshotTableRows(snapshot, tableKey, legacyKey, rootKey) {
  const snap = snapshot && typeof snapshot === 'object' ? snapshot : {};
  const schemaObj = snap.schema && typeof snap.schema === 'object' ? snap.schema : {};

  if (schemaObj && Object.prototype.hasOwnProperty.call(schemaObj, tableKey)) {
    return schemaObj[tableKey];
  }
  if (schemaObj && legacyKey && Object.prototype.hasOwnProperty.call(schemaObj, legacyKey)) {
    return schemaObj[legacyKey];
  }
  if (snap && rootKey && Object.prototype.hasOwnProperty.call(snap, rootKey)) {
    return snap[rootKey];
  }
  return [];
}

async function _replaceStoredRelation(runner, relationName, headVars, schemaText, rows) {
  const vars = Array.isArray(headVars) ? headVars : [];
  const schema = String(schemaText || '').trim();
  if (!vars.length) throw new Error('headVars is required');
  if (!schema) throw new Error('schemaText is required');

  const script = `
?[${vars.join(', ')}] <- $rows

:replace ${relationName} { ${schema} }
  `.trim();

  await runRows(runner, script, { rows: Array.isArray(rows) ? rows : [] });
}

function _formatRollbackDiagnosticsSummary(diagnostics) {
  const list = Array.isArray(diagnostics) ? diagnostics : [];
  if (!list.length) return '';

  const maxEntities = 3;
  const maxErrors = 2;
  const parts = [];
  for (const d of list.slice(0, maxEntities)) {
    if (!d || typeof d !== 'object') continue;
    const id = String(d.entityId || '').trim();
    const errors = Array.isArray(d.errors) ? d.errors : [];
    const sample = errors.slice(0, maxErrors).map((e) => String(e)).join('; ');
    if (id) parts.push(`${id}: ${sample}`);
  }
  const suffix = list.length > maxEntities ? ` (+${list.length - maxEntities} more)` : '';
  return parts.join(' | ') + suffix;
}

async function rollbackSchema(runner, targetVersion, options) {
  const tv = Number(targetVersion);
  if (!Number.isFinite(tv) || tv <= 0) throw new Error('targetVersion must be a positive number');

  const opts = options && typeof options === 'object' ? options : {};
  const strict = opts.strict !== false;

  // Read current state outside the transaction for stable return fields.
  const state0 = await getSchemaState(runner);
  const fromVersion = Number(state0.currentVersion);

  if (fromVersion === tv) {
    // Still invalidate alias caches so callers see any out-of-band alias changes.
    try {
      _aliasCacheByRunner.delete(runner);
    } catch (_) {
    }
    return { ok: true, strict, targetVersion: tv, fromVersion, diagnostics: [] };
  }

  const result = await _withWriteTxIfPossible(runner, async (txRunner) => {
    const snapshot = await readSchemaSnapshot(txRunner, tv);
    if (!snapshot) {
      throw new Error(`Missing schema snapshot for version=${tv}; cannot rollback`);
    }

    // Restore schema definitions from snapshot.
    const omTypeRows = _getSnapshotTableRows(snapshot, 'om_type', 'types', 'types');
    const omMixinRows = _getSnapshotTableRows(snapshot, 'om_mixin', 'mixins', null);
    const omTypeMixinRows = _getSnapshotTableRows(snapshot, 'om_type_mixin', 'typeMixins', null);
    const omAttrDefRows = _getSnapshotTableRows(snapshot, 'om_attr_def', 'attrDefs', 'attrs');
    const omRelDefRows = _getSnapshotTableRows(snapshot, 'om_rel_def', 'relDefs', 'rels');

    const omAliasTypeRows = _getSnapshotTableRows(snapshot, 'om_alias_type', 'aliasTypes', null);
    const omAliasRelRows = _getSnapshotTableRows(snapshot, 'om_alias_rel', 'aliasRels', null);
    const omAliasAttrRows = _getSnapshotTableRows(snapshot, 'om_alias_attr', 'aliasAttrs', null);

    await _replaceStoredRelation(txRunner, 'om_type', ['name', 'description', 'parent_type'], 'name => description, parent_type', omTypeRows);
    await _replaceStoredRelation(txRunner, 'om_mixin', ['name', 'description'], 'name => description', omMixinRows);
    await _replaceStoredRelation(txRunner, 'om_type_mixin', ['type_name', 'mixin_name'], 'type_name, mixin_name', omTypeMixinRows);
    await _replaceStoredRelation(
      txRunner,
      'om_attr_def',
      ['type_name', 'attr_name', 'value_type', 'required'],
      'type_name, attr_name => value_type, required',
      omAttrDefRows
    );
    await _replaceStoredRelation(
      txRunner,
      'om_rel_def',
      ['rel_name', 'from_type', 'to_type', 'directed'],
      'rel_name => from_type, to_type, directed',
      omRelDefRows
    );

    await _replaceStoredRelation(txRunner, 'om_alias_type', ['alias', 'canonical'], 'alias => canonical', omAliasTypeRows);
    await _replaceStoredRelation(txRunner, 'om_alias_rel', ['alias', 'canonical'], 'alias => canonical', omAliasRelRows);
    await _replaceStoredRelation(
      txRunner,
      'om_alias_attr',
      ['type_name', 'alias_attr', 'canonical_attr'],
      'type_name, alias_attr => canonical_attr',
      omAliasAttrRows
    );

    // Existential rules: legacy snapshots lack this section -> restore as empty.
    const omExistentialRuleRows = _getSnapshotTableRows(snapshot, 'om_existential_rule_def', 'existentialRules', null);
    try {
      await _replaceStoredRelation(
        txRunner,
        'om_existential_rule_def',
        ['rule_name', 'spec_json', 'mode', 'message', 'enabled'],
        'rule_name => spec_json, mode, message, enabled',
        omExistentialRuleRows
      );
    } catch (e) {
      if (!_isStoredRelationMissingError(e)) throw e;
    }

    // Optional: restore permission policy metadata if present.
    const perm = snapshot && typeof snapshot === 'object' ? snapshot.perm : null;
    if (perm && typeof perm === 'object') {
      const actions = Array.isArray(perm.actions) ? perm.actions : [];
      const policies = Array.isArray(perm.policies) ? perm.policies : [];
      const abacRules = Array.isArray(perm.abacRules) ? perm.abacRules : [];
      const pathRules = Array.isArray(perm.pathRules) ? perm.pathRules : [];

      const tryReplacePerm = async (relName, head, schema, rows) => {
        try {
          await _replaceStoredRelation(txRunner, relName, head, schema, rows);
        } catch (e) {
          if (_isStoredRelationMissingError(e)) return;
          throw e;
        }
      };

      // If any om_perm_* relations are missing, skip gracefully.
      await tryReplacePerm('om_perm_action', ['action', 'description'], 'action => description', actions);
      await tryReplacePerm(
        'om_perm_policy',
        ['policy_id', 'effect', 'action', 'resource_type', 'enabled', 'description'],
        'policy_id => effect, action, resource_type, enabled, description',
        policies
      );
      await tryReplacePerm(
        'om_perm_abac_rule',
        ['policy_id', 'left_ref', 'op', 'right_ref'],
        'policy_id, left_ref, op, right_ref',
        abacRules
      );
      await tryReplacePerm('om_perm_path_rule', ['policy_id', 'path'], 'policy_id, path', pathRules);
    }

    // Invalidate alias caches in the transaction runner before validating.
    try {
      _aliasCacheByRunner.delete(txRunner);
    } catch (_) {
    }

    // Validate entities against the restored schema.
    const diagnostics = [];
    const entityRows = await runDslRows(
      txRunner,
      query()
        .select(['id', 'type_name'])
        .fromStored('om_entity', {
          id: dsl.var('id'),
          type_name: dsl.var('type_name'),
          label: dsl.var('_label'),
        })
        .order('id')
    );

    for (const [entityId, storedTypeName] of entityRows) {
      const id = String(entityId || '').trim();
      if (!id) continue;
      const errors = [];
      const canonicalTypeName = await resolveType(txRunner, storedTypeName);
      const typeExists = await _typeExists(txRunner, canonicalTypeName);
      if (!typeExists) {
        errors.push(`Unknown type '${canonicalTypeName}'`);
      }

      const v = await validateEntity(txRunner, id);
      if (v && Array.isArray(v.errors)) {
        for (const msg of v.errors) errors.push(String(msg));
      }
      if (errors.length) {
        diagnostics.push({ entityId: id, typeName: canonicalTypeName, errors });
        if (diagnostics.length >= 50) break;
      }
    }

    if (diagnostics.length && strict) {
      const summary = _formatRollbackDiagnosticsSummary(diagnostics);
      throw new Error(
        `Strict rollback blocked: existing data violates schema v${tv} (${diagnostics.length} entities). ` +
          (summary ? `Examples: ${summary}` : '')
      );
    }

    // Update schema state to target version + checksum.
    const checksum = _computeSchemaChecksum(snapshot);
    await runDslRows(
      txRunner,
      query()
        .input({
          id: param('id', 'default'),
          current_version: param('current_version', tv),
          current_checksum: param('current_checksum', checksum),
        })
        .put('om_schema_state', ['id'], ['current_version', 'current_checksum'])
    );

    return { ok: true, strict, targetVersion: tv, fromVersion, diagnostics };
  });

  // Invalidate alias caches for the original runner so subsequent reads resolve fresh.
  try {
    _aliasCacheByRunner.delete(runner);
  } catch (_) {
  }

  return result;
}

// P1/WAVE-P1-02: Alias resolution helpers (stored relations: om_alias_type/rel/attr)
// These APIs canonicalize names and provide deterministic read fallbacks.
const _aliasCacheByRunner = new WeakMap();

function _getAliasCacheForRunner(runner) {
  if (!runner || (typeof runner !== 'object' && typeof runner !== 'function')) return null;
  let cache = _aliasCacheByRunner.get(runner);
  if (!cache) {
    cache = {
      // direct mapping: alias -> next (string) or null
      typeNext: new Map(),
      relNext: new Map(),
      attrNextByType: new Map(),

      // whether we have loaded the full mapping tables
      typeLoadedAll: false,
      relLoadedAll: false,
      attrLoadedAllByType: new Set(),

      // final canonical results: name -> canonical
      typeFinal: new Map(),
      relFinal: new Map(),
      attrFinalByType: new Map(),

      // reverse listings for deterministic read fallback
      typeAliasesForCanonical: new Map(),
      relAliasesForCanonical: new Map(),
      attrAliasesForCanonicalByType: new Map(),
    };
    _aliasCacheByRunner.set(runner, cache);
  }
  return cache;
}

// Out-of-band alias administration (e.g. seeding om_alias_* rows directly)
// must drop the per-runner resolution cache or long-lived runners keep
// resolving pre-rename names.
function invalidateAliasCache(runner) {
  try {
    _aliasCacheByRunner.delete(runner);
  } catch (_) {
  }
}

function _requireAliasName(value, name) {
  const normalized = String(value || '').trim();
  if (!normalized) throw new Error(`${name} is required`);
  return normalized;
}

async function defineTypeAlias(runner, alias, canonical) {
  const aliasName = _requireAliasName(alias, 'alias');
  const canonicalName = _requireAliasName(canonical, 'canonical');
  await runDslRows(
    runner,
    query()
      .input({
        alias: param('alias', aliasName),
        canonical: param('canonical', canonicalName),
      })
      .put('om_alias_type', ['alias'], ['canonical'])
  );
  invalidateAliasCache(runner);
}

async function defineRelationAlias(runner, alias, canonical) {
  const aliasName = _requireAliasName(alias, 'alias');
  const canonicalName = _requireAliasName(canonical, 'canonical');
  await runDslRows(
    runner,
    query()
      .input({
        alias: param('alias', aliasName),
        canonical: param('canonical', canonicalName),
      })
      .put('om_alias_rel', ['alias'], ['canonical'])
  );
  invalidateAliasCache(runner);
}

async function defineAttributeAlias(runner, typeName, aliasAttr, canonicalAttr) {
  const type = _requireAliasName(typeName, 'typeName');
  const aliasName = _requireAliasName(aliasAttr, 'aliasAttr');
  const canonicalName = _requireAliasName(canonicalAttr, 'canonicalAttr');
  await runDslRows(
    runner,
    query()
      .input({
        type_name: param('type_name', type),
        alias_attr: param('alias_attr', aliasName),
        canonical_attr: param('canonical_attr', canonicalName),
      })
      .put('om_alias_attr', ['type_name', 'alias_attr'], ['canonical_attr'])
  );
  invalidateAliasCache(runner);
}

function _formatAliasCycleError(kind, path, extra) {
  const chain = Array.isArray(path) ? path.join(' -> ') : String(path || '');
  const suffix = extra ? ` (${extra})` : '';
  return new Error(`Alias cycle detected for ${kind}${suffix}: ${chain}`);
}

async function _lookupTypeAliasNext(runner, alias) {
  const cache = _getAliasCacheForRunner(runner);
  if (cache && cache.typeNext.has(alias)) return cache.typeNext.get(alias);
  if (cache && cache.typeLoadedAll) return null;
  let rows;
  try {
    rows = await runDslRows(
      runner,
      query()
        .select(['canonical'])
        .fromStored('om_alias_type', {
          alias: param('alias', alias),
          canonical: dsl.var('canonical'),
        })
        .limit(1)
    );
  } catch (e) {
    if (_isStoredRelationMissingError(e)) {
      if (cache) cache.typeLoadedAll = true;
      return null;
    }
    throw e;
  }
  const next = rows.length ? String(rows[0][0] || '').trim() : '';
  const normalized = next ? next : null;
  if (cache) cache.typeNext.set(alias, normalized);
  return normalized;
}

async function _lookupRelAliasNext(runner, alias) {
  const cache = _getAliasCacheForRunner(runner);
  if (cache && cache.relNext.has(alias)) return cache.relNext.get(alias);
  if (cache && cache.relLoadedAll) return null;
  let rows;
  try {
    rows = await runDslRows(
      runner,
      query()
        .select(['canonical'])
        .fromStored('om_alias_rel', {
          alias: param('alias', alias),
          canonical: dsl.var('canonical'),
        })
        .limit(1)
    );
  } catch (e) {
    if (_isStoredRelationMissingError(e)) {
      if (cache) cache.relLoadedAll = true;
      return null;
    }
    throw e;
  }
  const next = rows.length ? String(rows[0][0] || '').trim() : '';
  const normalized = next ? next : null;
  if (cache) cache.relNext.set(alias, normalized);
  return normalized;
}

async function _lookupAttrAliasNext(runner, canonicalTypeName, aliasAttr) {
  const cache = _getAliasCacheForRunner(runner);
  if (cache) {
    if (!cache.attrNextByType.has(canonicalTypeName)) cache.attrNextByType.set(canonicalTypeName, new Map());
    const typeMap = cache.attrNextByType.get(canonicalTypeName);
    if (typeMap.has(aliasAttr)) return typeMap.get(aliasAttr);
    if (cache.attrLoadedAllByType.has(canonicalTypeName)) return null;
  }
  let rows;
  try {
    rows = await runDslRows(
      runner,
      query()
        .select(['canonical_attr'])
        .fromStored('om_alias_attr', {
          type_name: param('type_name', canonicalTypeName),
          alias_attr: param('alias_attr', aliasAttr),
          canonical_attr: dsl.var('canonical_attr'),
        })
        .limit(1)
    );
  } catch (e) {
    if (_isStoredRelationMissingError(e)) {
      if (cache) cache.attrLoadedAllByType.add(canonicalTypeName);
      return null;
    }
    throw e;
  }
  const next = rows.length ? String(rows[0][0] || '').trim() : '';
  const normalized = next ? next : null;
  if (cache) {
    const typeMap = cache.attrNextByType.get(canonicalTypeName);
    typeMap.set(aliasAttr, normalized);
  }
  return normalized;
}

async function _preloadTypeAliases(runner) {
  const cache = _getAliasCacheForRunner(runner);
  if (!cache || cache.typeLoadedAll) return;
  let rows;
  try {
    rows = await runDslRows(
      runner,
      query()
        .select(['alias', 'canonical'])
        .fromStored('om_alias_type', {
          alias: dsl.var('alias'),
          canonical: dsl.var('canonical'),
        })
    );
  } catch (e) {
    if (_isStoredRelationMissingError(e)) {
      cache.typeLoadedAll = true;
      return;
    }
    throw e;
  }
  for (const [a, c] of rows) {
    const alias = String(a || '').trim();
    const canon = String(c || '').trim();
    if (!alias) continue;
    cache.typeNext.set(alias, canon ? canon : null);
  }
  cache.typeLoadedAll = true;
}

async function _preloadRelAliases(runner) {
  const cache = _getAliasCacheForRunner(runner);
  if (!cache || cache.relLoadedAll) return;
  let rows;
  try {
    rows = await runDslRows(
      runner,
      query()
        .select(['alias', 'canonical'])
        .fromStored('om_alias_rel', {
          alias: dsl.var('alias'),
          canonical: dsl.var('canonical'),
        })
    );
  } catch (e) {
    if (_isStoredRelationMissingError(e)) {
      cache.relLoadedAll = true;
      return;
    }
    throw e;
  }
  for (const [a, c] of rows) {
    const alias = String(a || '').trim();
    const canon = String(c || '').trim();
    if (!alias) continue;
    cache.relNext.set(alias, canon ? canon : null);
  }
  cache.relLoadedAll = true;
}

async function _preloadAttrAliasesForType(runner, canonicalTypeName) {
  const tn = String(canonicalTypeName || '').trim();
  if (!tn) return;
  const cache = _getAliasCacheForRunner(runner);
  if (!cache) return;
  if (cache.attrLoadedAllByType.has(tn)) return;
  if (!cache.attrNextByType.has(tn)) cache.attrNextByType.set(tn, new Map());
  const typeMap = cache.attrNextByType.get(tn);
  let rows;
  try {
    rows = await runDslRows(
      runner,
      query()
        .select(['alias_attr', 'canonical_attr'])
        .fromStored('om_alias_attr', {
          type_name: param('type_name', tn),
          alias_attr: dsl.var('alias_attr'),
          canonical_attr: dsl.var('canonical_attr'),
        })
    );
  } catch (e) {
    if (_isStoredRelationMissingError(e)) {
      cache.attrLoadedAllByType.add(tn);
      return;
    }
    throw e;
  }
  for (const [a, c] of rows) {
    const alias = String(a || '').trim();
    const canon = String(c || '').trim();
    if (!alias) continue;
    typeMap.set(alias, canon ? canon : null);
  }
  cache.attrLoadedAllByType.add(tn);
}

async function resolveType(runner, typeName) {
  const start = String(typeName || '').trim();
  if (!start) throw new Error('typeName is required');
  const cache = _getAliasCacheForRunner(runner);
  if (cache && cache.typeFinal.has(start)) return cache.typeFinal.get(start);

  const visited = new Set();
  const path = [];
  let current = start;

  while (true) {
    if (cache && cache.typeFinal.has(current)) {
      const final = cache.typeFinal.get(current);
      if (cache) {
        for (const p of path) cache.typeFinal.set(p, final);
        cache.typeFinal.set(start, final);
      }
      return final;
    }
    if (visited.has(current)) {
      path.push(current);
      throw _formatAliasCycleError('type', path);
    }
    visited.add(current);
    path.push(current);

    const next = await _lookupTypeAliasNext(runner, current);
    if (!next) {
      const final = current;
      if (cache) {
        for (const p of path) cache.typeFinal.set(p, final);
        cache.typeFinal.set(start, final);
      }
      return final;
    }
    current = next;
  }
}

async function resolveRel(runner, relName) {
  const start = String(relName || '').trim();
  if (!start) throw new Error('relName is required');
  const cache = _getAliasCacheForRunner(runner);
  if (cache && cache.relFinal.has(start)) return cache.relFinal.get(start);

  const visited = new Set();
  const path = [];
  let current = start;

  while (true) {
    if (cache && cache.relFinal.has(current)) {
      const final = cache.relFinal.get(current);
      if (cache) {
        for (const p of path) cache.relFinal.set(p, final);
        cache.relFinal.set(start, final);
      }
      return final;
    }
    if (visited.has(current)) {
      path.push(current);
      throw _formatAliasCycleError('relation', path);
    }
    visited.add(current);
    path.push(current);

    const next = await _lookupRelAliasNext(runner, current);
    if (!next) {
      const final = current;
      if (cache) {
        for (const p of path) cache.relFinal.set(p, final);
        cache.relFinal.set(start, final);
      }
      return final;
    }
    current = next;
  }
}

async function _resolveAttrForCanonicalType(runner, canonicalTypeName, attrName) {
  const start = String(attrName || '').trim();
  if (!start) throw new Error('attrName is required');
  const cache = _getAliasCacheForRunner(runner);
  if (cache) {
    if (!cache.attrFinalByType.has(canonicalTypeName)) cache.attrFinalByType.set(canonicalTypeName, new Map());
    const typeFinalMap = cache.attrFinalByType.get(canonicalTypeName);
    if (typeFinalMap.has(start)) return typeFinalMap.get(start);
  }

  const visited = new Set();
  const path = [];
  let current = start;
  while (true) {
    if (cache) {
      const typeFinalMap = cache.attrFinalByType.get(canonicalTypeName);
      if (typeFinalMap && typeFinalMap.has(current)) {
        const final = typeFinalMap.get(current);
        for (const p of path) typeFinalMap.set(p, final);
        typeFinalMap.set(start, final);
        return final;
      }
    }
    if (visited.has(current)) {
      path.push(current);
      throw _formatAliasCycleError('attribute', path, `type '${canonicalTypeName}'`);
    }
    visited.add(current);
    path.push(current);

    const next = await _lookupAttrAliasNext(runner, canonicalTypeName, current);
    if (!next) {
      const final = current;
      if (cache) {
        const typeFinalMap = cache.attrFinalByType.get(canonicalTypeName);
        for (const p of path) typeFinalMap.set(p, final);
        typeFinalMap.set(start, final);
      }
      return final;
    }
    current = next;
  }
}

async function resolveAttr(runner, typeName, attrName) {
  const tn0 = String(typeName || '').trim();
  const an0 = String(attrName || '').trim();
  if (!tn0) throw new Error('typeName is required');
  if (!an0) throw new Error('attrName is required');
  const tn = await resolveType(runner, tn0);
  return _resolveAttrForCanonicalType(runner, tn, an0);
}

function _resolveAliasChainInMap(kind, start, nextMap, resolvedCache, extra) {
  const visited = new Set();
  const path = [];
  let current = start;
  while (true) {
    if (resolvedCache && resolvedCache.has(current)) {
      const final = resolvedCache.get(current);
      if (resolvedCache) {
        for (const p of path) resolvedCache.set(p, final);
      }
      return final;
    }
    if (visited.has(current)) {
      path.push(current);
      throw _formatAliasCycleError(kind, path, extra);
    }
    visited.add(current);
    path.push(current);
    const next = nextMap.get(current);
    if (!next) {
      const final = current;
      if (resolvedCache) {
        for (const p of path) resolvedCache.set(p, final);
      }
      return final;
    }
    current = next;
  }
}

async function _listTypeAliasesForCanonical(runner, canonicalTypeName) {
  const target = String(canonicalTypeName || '').trim();
  if (!target) return [];
  const cache = _getAliasCacheForRunner(runner);
  if (cache && cache.typeAliasesForCanonical.has(target)) {
    return cache.typeAliasesForCanonical.get(target).slice();
  }

  await _preloadTypeAliases(runner);
  const nextMap = new Map();
  if (cache) {
    for (const [alias, canon] of cache.typeNext.entries()) {
      if (alias && canon) nextMap.set(alias, canon);
    }
  }
  const resolvedCache = new Map();
  const out = [];
  for (const alias of nextMap.keys()) {
    try {
      const final = _resolveAliasChainInMap('type', alias, nextMap, resolvedCache);
      if (final === target) out.push(alias);
    } catch (_) {
      // Skip aliases with cyclic resolution to avoid breaking reads.
    }
  }
  out.sort((l, r) => l.localeCompare(r));
  if (cache) cache.typeAliasesForCanonical.set(target, out.slice());
  return out;
}

async function _listRelAliasesForCanonical(runner, canonicalRelName) {
  const target = String(canonicalRelName || '').trim();
  if (!target) return [];
  const cache = _getAliasCacheForRunner(runner);
  if (cache && cache.relAliasesForCanonical.has(target)) {
    return cache.relAliasesForCanonical.get(target).slice();
  }

  await _preloadRelAliases(runner);
  const nextMap = new Map();
  if (cache) {
    for (const [alias, canon] of cache.relNext.entries()) {
      if (alias && canon) nextMap.set(alias, canon);
    }
  }
  const resolvedCache = new Map();
  const out = [];
  for (const alias of nextMap.keys()) {
    try {
      const final = _resolveAliasChainInMap('relation', alias, nextMap, resolvedCache);
      if (final === target) out.push(alias);
    } catch (_) {
      // Skip aliases with cyclic resolution to avoid breaking reads.
    }
  }
  out.sort((l, r) => l.localeCompare(r));
  if (cache) cache.relAliasesForCanonical.set(target, out.slice());
  return out;
}

async function _listAttrAliasesForCanonical(runner, canonicalTypeName, canonicalAttrName) {
  const tn = String(canonicalTypeName || '').trim();
  const target = String(canonicalAttrName || '').trim();
  if (!tn || !target) return [];
  const cache = _getAliasCacheForRunner(runner);
  const key = `${tn}::${target}`;
  if (cache) {
    if (!cache.attrAliasesForCanonicalByType.has(tn)) cache.attrAliasesForCanonicalByType.set(tn, new Map());
    const typeMap = cache.attrAliasesForCanonicalByType.get(tn);
    if (typeMap.has(key)) return typeMap.get(key).slice();
  }

  await _preloadAttrAliasesForType(runner, tn);
  const nextMap = new Map();
  if (cache) {
    const map = cache.attrNextByType.get(tn);
    if (map) {
      for (const [alias, canon] of map.entries()) {
        if (alias && canon) nextMap.set(alias, canon);
      }
    }
  }
  const resolvedCache = new Map();
  const out = [];
  for (const alias of nextMap.keys()) {
    try {
      const final = _resolveAliasChainInMap('attribute', alias, nextMap, resolvedCache, `type '${tn}'`);
      if (final === target) out.push(alias);
    } catch (_) {
      // Skip aliases with cyclic resolution to avoid breaking reads.
    }
  }
  out.sort((l, r) => l.localeCompare(r));
  if (cache) {
    const typeMap = cache.attrAliasesForCanonicalByType.get(tn);
    typeMap.set(key, out.slice());
  }
  return out;
}

async function _typeExists(runner, name) {
  const rows = await runDslRows(
    runner,
    query()
      .select(['n'])
      .fromStored('om_type', {
        name: param('name', name),
        description: dsl.var('_desc'),
        parent_type: dsl.var('_pt'),
      })
      .atom('n = $name')
      .limit(1)
  );
  return rows.length > 0;
}

async function _getParentType(runner, typeName) {
  const rows = await runDslRows(
    runner,
    query()
      .select(['parent_type'])
      .fromStored('om_type', {
        name: param('name', typeName),
        description: dsl.var('_desc'),
        parent_type: dsl.var('parent_type'),
      })
      .limit(1)
  );
  if (!rows.length) return null;
  const pt = rows[0][0];
  return (pt === null || pt === '' || pt === undefined) ? null : pt;
}

async function _getAncestorList(runner, typeName) {
  const ancestors = [];
  const visited = new Set();
  let current = typeName;
  while (current) {
    const parent = await _getParentType(runner, current);
    if (!parent) break;
    if (visited.has(parent)) break;
    visited.add(parent);
    ancestors.push(parent);
    current = parent;
  }
  return ancestors;
}

async function _mixinExists(runner, name) {
  const rows = await runDslRows(
    runner,
    query()
      .select(['n'])
      .fromStored('om_mixin', {
        name: param('name', name),
        description: dsl.var('_desc'),
      })
      .atom('n = $name')
      .limit(1)
  );
  return rows.length > 0;
}

async function _getTypeMixins(runner, typeName) {
  const rows = await runDslRows(
    runner,
    query()
      .select(['mixin_name'])
      .fromStored('om_type_mixin', {
        type_name: param('type_name', typeName),
        mixin_name: dsl.var('mixin_name'),
      })
  );
  return rows.map(([m]) => m);
}

async function defineType(runner, name, description, options) {
  const opts = options || {};
  const hasParentOption = Object.prototype.hasOwnProperty.call(opts, 'parentType');
  const hasMixinsOption = Object.prototype.hasOwnProperty.call(opts, 'mixins');
  const exists = await _typeExists(runner, name);

  let parentType = null;
  if (hasParentOption) {
    const raw = opts.parentType;
    parentType = raw === null || raw === undefined || String(raw).trim() === '' ? null : String(raw);
  } else if (exists) {
    parentType = await _getParentType(runner, name);
  }

  const mixins = hasMixinsOption && Array.isArray(opts.mixins) ? opts.mixins : null;

  if (parentType) {
    const parentExists = await _typeExists(runner, parentType);
    if (!parentExists) {
      throw new Error(`Parent type '${parentType}' does not exist`);
    }
    // Circular inheritance detection: walk parent's ancestors
    const parentAncestors = await _getAncestorList(runner, parentType);
    if (parentAncestors.includes(name) || parentType === name) {
      throw new Error(`Circular inheritance detected: '${name}' -> '${parentType}'`);
    }
  }

  if (mixins) {
    for (const mixinName of mixins) {
      const mixinExists = await _mixinExists(runner, mixinName);
      if (!mixinExists) {
        throw new Error(`Mixin '${mixinName}' does not exist`);
      }
    }
  }

  await runDslRows(
    runner,
    query()
      .input({
        name: param('name', name),
        description: param('description', description),
        parent_type: param('parent_type', parentType),
      })
      .put('om_type', ['name'], ['description', 'parent_type'])
  );

  if (mixins) {
    const existingLinks = await _getTypeMixins(runner, name);
    for (const mixinName of existingLinks) {
      await runDslRows(
        runner,
        query()
          .input({
            type_name: param('type_name', name),
            mixin_name: param('mixin_name', mixinName),
          })
          .rm('om_type_mixin', ['type_name', 'mixin_name'])
      );
    }

    for (const mixinName of mixins) {
      await runDslRows(
        runner,
        query()
          .input({
            type_name: param('type_name', name),
            mixin_name: param('mixin_name', mixinName),
          })
          .put('om_type_mixin', ['type_name', 'mixin_name'], [])
      );
    }
  }
}

async function defineMixin(runner, name, description) {
  await runDslRows(
    runner,
    query()
      .input({
        name: param('name', name),
        description: param('description', description),
      })
      .put('om_mixin', ['name'], ['description'])
  );
}

async function getAncestors(runner, typeName) {
  return _getAncestorList(runner, typeName);
}

async function getDescendants(runner, typeName) {
  const allRows = await runDslRows(
    runner,
    query()
      .select(['name', 'parent_type'])
      .fromStored('om_type', {
        name: dsl.var('name'),
        description: dsl.var('_desc'),
        parent_type: dsl.var('parent_type'),
      })
  );
  const childrenMap = {};
  for (const [n, pt] of allRows) {
    if (pt && pt !== '') {
      if (!childrenMap[pt]) childrenMap[pt] = [];
      childrenMap[pt].push(n);
    }
  }
  const result = [];
  const queue = [typeName];
  const visited = new Set([typeName]);
  while (queue.length) {
    const current = queue.shift();
    const children = childrenMap[current] || [];
    for (const child of children) {
      if (!visited.has(child)) {
        visited.add(child);
        result.push(child);
        queue.push(child);
      }
    }
  }
  return result;
}

async function isSubtypeOf(runner, childType, parentType) {
  if (childType === parentType) return true;
  const ancestors = await _getAncestorList(runner, childType);
  return ancestors.includes(parentType);
}

async function getTypeHierarchy(runner) {
  const allRows = await runDslRows(
    runner,
    query()
      .select(['name', 'description', 'parent_type'])
      .fromStored('om_type', {
        name: dsl.var('name'),
        description: dsl.var('description'),
        parent_type: dsl.var('parent_type'),
      })
  );
  const mixinRows = await runDslRows(
    runner,
    query()
      .select(['type_name', 'mixin_name'])
      .fromStored('om_type_mixin', {
        type_name: dsl.var('type_name'),
        mixin_name: dsl.var('mixin_name'),
      })
  );

  const mixinsByType = new Map();
  for (const [typeName, mixinName] of mixinRows) {
    if (!mixinsByType.has(typeName)) mixinsByType.set(typeName, []);
    mixinsByType.get(typeName).push(mixinName);
  }

  const types = {};
  for (const [name, description, parentType] of allRows) {
    const pt = (parentType === null || parentType === '' || parentType === undefined) ? null : parentType;
    types[name] = {
      name,
      description,
      parentType: pt,
      mixins: mixinsByType.get(name) || [],
      children: [],
    };
  }

  for (const node of Object.values(types)) {
    if (node.parentType && types[node.parentType]) {
      types[node.parentType].children.push(node.name);
    }
  }

  const roots = Object.values(types)
    .filter((node) => !node.parentType || !types[node.parentType])
    .map((node) => node.name)
    .sort();

  for (const node of Object.values(types)) {
    node.children.sort();
    node.mixins.sort();
  }

  return { types, roots };
}

async function defineAttribute(runner, typeName, attrName, valueType, required = false, description) {
  const canonicalTypeName = await resolveType(runner, typeName);
  const canonicalAttrName = await _resolveAttrForCanonicalType(runner, canonicalTypeName, attrName);

  if (!ALLOWED_VALUE_TYPES.has(valueType)) {
    throw new Error(`Unsupported attribute value type '${valueType}'`);
  }

  const inheritedDefs = await _getInheritedAttributeDefinitions(runner, canonicalTypeName);
  const inherited = inheritedDefs.get(canonicalAttrName);
  if (inherited) {
    if (inherited.valueType !== valueType) {
      throw new Error(
        `Cannot change value_type of '${canonicalAttrName}' (inherited as ${inherited.valueType})`
      );
    }
    if (inherited.required && !required) {
      throw new Error(
        `Cannot loosen required constraint of '${canonicalAttrName}' (inherited as required)`
      );
    }
  }

  await runDslRows(
    runner,
    query()
      .input({
        type_name: param('type_name', canonicalTypeName),
        attr_name: param('attr_name', canonicalAttrName),
        value_type: param('value_type', valueType),
        required: param('required', required),
      })
      .put('om_attr_def', ['type_name', 'attr_name'], ['value_type', 'required'])
  );

  if (description !== undefined && String(description).trim() !== '') {
    await runDslRows(
      runner,
      query()
        .input({
          type_name: param('type_name', canonicalTypeName),
          attr_name: param('attr_name', canonicalAttrName),
          description: param('description', String(description).trim()),
        })
        .put('om_attr_desc', ['type_name', 'attr_name'], ['description'])
    );
  }
}

async function defineRelation(runner, relName, fromType, toType, directed = true, description) {
  const canonicalRelName = await resolveRel(runner, relName);
  const canonicalFromType = await resolveType(runner, fromType);
  const canonicalToType = await resolveType(runner, toType);

  await runDslRows(
    runner,
    query()
      .input({
        rel_name: param('rel_name', canonicalRelName),
        from_type: param('from_type', canonicalFromType),
        to_type: param('to_type', canonicalToType),
        directed: param('directed', directed),
      })
      .put('om_rel_def', ['rel_name'], ['from_type', 'to_type', 'directed'])
  );

  if (description !== undefined && String(description).trim() !== '') {
    await runDslRows(
      runner,
      query()
        .input({
          rel_name: param('rel_name', canonicalRelName),
          description: param('description', String(description).trim()),
        })
        .put('om_rel_desc', ['rel_name'], ['description'])
    );
  }
}

async function createEntity(runner, id, typeName, label) {
  const canonicalTypeName = await resolveType(runner, typeName);
  if (!(await _typeExists(runner, canonicalTypeName))) {
    throw new Error(`Type '${canonicalTypeName}' does not exist`);
  }
  await runDslRows(
    runner,
    query()
      .input({ id: param('id', id), type_name: param('type_name', canonicalTypeName), label: param('label', label) })
      .insert('om_entity', ['id'], ['type_name', 'label'])
  );
}

async function upsertEntity(runner, id, typeName, label) {
  const canonicalTypeName = await resolveType(runner, typeName);
  if (!(await _typeExists(runner, canonicalTypeName))) {
    throw new Error(`Type '${canonicalTypeName}' does not exist`);
  }
  await runDslRows(
    runner,
    query()
      .input({ id: param('id', id), type_name: param('type_name', canonicalTypeName), label: param('label', label) })
      .put('om_entity', ['id'], ['type_name', 'label'])
  );
}

async function deleteEntity(runner, entityId) {
  const id = String(entityId || '').trim();
  if (!id) throw new Error('entityId is required');

  await _withWriteTxIfPossible(runner, async (txRunner) => {
    // Physical deletion removes every temporal fact, not only the @ NOW projection.
    await runRows(
      txRunner,
      `
?[entity_id, attr_name, valid_time] :=
  *om_property{ entity_id, attr_name, valid_time, value: _value, tx_time: _tx_time },
  entity_id = $entity_id
:rm om_property {entity_id, attr_name, valid_time}
      `.trim(),
      { entity_id: id }
    );
    await runRows(
      txRunner,
      `
?[from_id, rel_name, to_id, valid_time] :=
  *om_edge{ from_id, rel_name, to_id, valid_time, props: _props, tx_time: _tx_time },
  from_id = $entity_id
?[from_id, rel_name, to_id, valid_time] :=
  *om_edge{ from_id, rel_name, to_id, valid_time, props: _props, tx_time: _tx_time },
  to_id = $entity_id
:rm om_edge {from_id, rel_name, to_id, valid_time}
      `.trim(),
      { entity_id: id }
    );
    await runRows(
      txRunner,
      '?[id] <- [[$entity_id]]\n:rm om_entity {id}',
      { entity_id: id }
    );
  });
}

async function getEntityType(runner, entityId) {
  const rows = await runDslRows(
    runner,
      query()
        .select(['type_name'])
        .fromStored('om_entity', {
          id: param('id', entityId),
          type_name: dsl.var('type_name'),
          label: dsl.var('_label'),
        })
      .limit(1)
  );
  if (!rows.length) {
    throw new Error(`Entity '${entityId}' does not exist`);
  }
  const stored = rows[0][0];
  return resolveType(runner, stored);
}

async function _getOwnAttributeDefinitions(runner, typeName) {
  const rows = await runDslRows(
    runner,
    query()
      .select(['attr_name', 'value_type', 'required'])
      .fromStored('om_attr_def', {
        type_name: param('type_name', typeName),
        attr_name: dsl.var('attr_name'),
        value_type: dsl.var('value_type'),
        required: dsl.var('required'),
      })
  );
  const descRows = await runDslRows(
    runner,
    query()
      .select(['attr_name', 'description'])
      .fromStored('om_attr_desc', {
        type_name: param('type_name', typeName),
        attr_name: dsl.var('attr_name'),
        description: dsl.var('description'),
      })
  );
  const descMap = new Map();
  for (const [attrName, desc] of descRows) {
    descMap.set(attrName, desc);
  }
  const definitions = new Map();
  for (const [attrName, valueType, required] of rows) {
    const def = { valueType, required: !!required };
    if (descMap.has(attrName)) {
      def.description = descMap.get(attrName);
    }
    definitions.set(attrName, def);
  }
  return definitions;
}

async function _getInheritedAttributeDefinitions(runner, typeName) {
  // Merge precedence: mixins (lowest) -> far ancestors -> near ancestors
  const ancestors = await _getAncestorList(runner, typeName);
  const mixins = await _getTypeMixins(runner, typeName);

  // Also collect mixins from ancestors.
  const allMixins = [...mixins];
  for (const ancestor of ancestors) {
    const ancestorMixins = await _getTypeMixins(runner, ancestor);
    for (const m of ancestorMixins) {
      if (!allMixins.includes(m)) allMixins.push(m);
    }
  }

  const merged = new Map();

  // 1. Mixins (lowest priority)
  for (const mixinName of allMixins) {
    const mixinDefs = await _getOwnAttributeDefinitions(runner, mixinName);
    for (const [attrName, def] of mixinDefs) {
      merged.set(attrName, { ...def });
    }
  }

  // 2. Far ancestors to near ancestors (reversed ancestor list = far first)
  const reversedAncestors = ancestors.slice().reverse();
  for (const ancestor of reversedAncestors) {
    const ancestorDefs = await _getOwnAttributeDefinitions(runner, ancestor);
    for (const [attrName, def] of ancestorDefs) {
      merged.set(attrName, { ...def });
    }
  }

  return merged;
}

async function getAttributeDefinitions(runner, typeName) {
  // Merge precedence: mixins (lowest) -> far ancestors -> near ancestors -> self (highest)
  const merged = await _getInheritedAttributeDefinitions(runner, typeName);

  // Self (highest priority)
  const selfDefs = await _getOwnAttributeDefinitions(runner, typeName);
  for (const [attrName, def] of selfDefs) {
    merged.set(attrName, { ...def });
  }

  return merged;
}

async function validatePropertyType(runner, entityId, attrName, value) {
  const typeName = await getEntityType(runner, entityId);
  const canonicalAttrName = await _resolveAttrForCanonicalType(runner, typeName, attrName);
  const definitions = await getAttributeDefinitions(runner, typeName);
  const definition = definitions.get(canonicalAttrName);
  if (!definition) {
    throw new Error(`Attribute '${canonicalAttrName}' is not defined for type '${typeName}'`);
  }

  if (definition.valueType === 'Validity') {
    const normalized = _normalizeValidityInput(value);
    if (!normalized) {
      throw new Error(
        `Type mismatch: attribute '${canonicalAttrName}' expects Validity, got ${inferValueType(value)}`
      );
    }
    return;
  }

  const actualType = inferValueType(value);
  if (definition.valueType !== actualType) {
    throw new Error(
      `Type mismatch: attribute '${canonicalAttrName}' expects ${definition.valueType}, got ${actualType}`
    );
  }
}

async function setProperty(runner, entityId, attrName, value, options) {
  const opts = options && typeof options === 'object' ? options : {};
  const skipConstraints = opts.skipConstraints === true;
  const requestedValidTime = Object.prototype.hasOwnProperty.call(opts, 'validTime')
    ? String(opts.validTime || '').trim()
    : '';

  const txTime = new Date().toISOString();
  // NOTE: default to CozoDB's "ASSERT" so that @ "NOW" reads within the same transaction see the write.
  const validTime = requestedValidTime ? requestedValidTime : 'ASSERT';

  await _withWriteTxIfPossible(runner, async (txRunner) => {
    const canonicalTypeName = await getEntityType(txRunner, entityId);
    const canonicalAttrName = await _resolveAttrForCanonicalType(txRunner, canonicalTypeName, attrName);

    // Disallow setting computed properties (handle defs registered under canonical or alias name).
    const rawAttrName = String(attrName || '').trim();
    const computedDefCanonical = await _resolveComputedDef(txRunner, canonicalTypeName, canonicalAttrName).catch(() => null);
    const computedDefRaw = rawAttrName && rawAttrName !== canonicalAttrName
      ? await _resolveComputedDef(txRunner, canonicalTypeName, rawAttrName).catch(() => null)
      : null;
    if (computedDefCanonical || computedDefRaw) {
      throw new Error(`Cannot set computed property '${String(canonicalAttrName)}'`);
    }

    await validatePropertyType(txRunner, entityId, canonicalAttrName, value);

    const definitions = await getAttributeDefinitions(txRunner, canonicalTypeName);
    const def = definitions.get(canonicalAttrName);
    if (def && def.valueType === 'Validity') {
      const normalized = _normalizeValidityInput(value);
      if (!normalized) {
        throw new Error(`Invalid Validity value for '${String(canonicalAttrName)}'`);
      }
      await runDslRows(
        txRunner,
        query()
          .input({
            entity_id: param('entity_id', entityId),
            attr_name: param('attr_name', canonicalAttrName),
            valid_time: param('valid_time', validTime),
            tx_time: param('tx_time', txTime),
            ts_us: param('ts_us', normalized.tsUs),
            is_assert: param('is_assert', normalized.isAssert),
            value: dsl.raw('validity($ts_us, $is_assert)'),
          })
          .put('om_property', ['entity_id', 'attr_name', 'valid_time'], ['value', 'tx_time'])
      );
    } else {
      await runDslRows(
        txRunner,
        query()
          .input({
            entity_id: param('entity_id', entityId),
            attr_name: param('attr_name', canonicalAttrName),
            valid_time: param('valid_time', validTime),
            tx_time: param('tx_time', txTime),
            value: param('value', value),
          })
          .put('om_property', ['entity_id', 'attr_name', 'valid_time'], ['value', 'tx_time'])
      );
    }

    if (!skipConstraints) {
      const result = await validateConstraints(txRunner, entityId, { types: ['conditional'] });
      if (!result.valid) {
        throw new Error(result.errors.join('; '));
      }
    }
  });
}

async function getProperty(runner, entityId, attrName) {
  const id = String(entityId || '').trim();
  const anRaw = String(attrName || '').trim();
  if (!id || !anRaw) return undefined;

  const typeName = await getEntityType(runner, id);
  await _preloadAttrAliasesForType(runner, typeName);
  const canonicalAttrName = await _resolveAttrForCanonicalType(runner, typeName, anRaw);

  const primaryRows = await runRows(
    runner,
    `
?[value] :=
  *om_property{ entity_id: $entity_id, attr_name: $attr_name, value @ "NOW" }
:limit 1
    `.trim(),
    { entity_id: id, attr_name: canonicalAttrName }
  );

  if (primaryRows.length) {
    return primaryRows[0][0];
  }

  const aliases = await _listAttrAliasesForCanonical(runner, typeName, canonicalAttrName);
  for (const aliasName of aliases) {
    const rows = await runRows(
      runner,
      `
?[value] :=
  *om_property{ entity_id: $entity_id, attr_name: $attr_name, value @ "NOW" }
:limit 1
      `.trim(),
      { entity_id: id, attr_name: aliasName }
    );
    if (rows.length) {
      return rows[0][0];
    }
  }

  // Fallback: computed properties are resolved lazily and are not stored.
  // If there is a computed definition for this type (or ancestors), invoke it.
  const def = await _resolveComputedDef(runner, typeName, canonicalAttrName).catch(() => null);
  const defRaw = !def && anRaw && anRaw !== canonicalAttrName
    ? await _resolveComputedDef(runner, typeName, anRaw).catch(() => null)
    : null;
  const chosen = def || defRaw;
  if (!chosen || typeof chosen.computeFn !== 'function') {
    return undefined;
  }

  const ctx = {
    runner,
    entityId: id,
    typeName,
    getProperty: async (name) => getProperty(runner, id, name),
    getNeighbors: async (relName, direction) => getNeighbors(runner, id, relName, direction),
  };

  return chosen.computeFn(ctx);
}

function _parseTimestampToMicros(value, fieldName) {
  if (value == null) return null;
  const raw = String(value).trim();
  if (!raw) return null;

  // Allow coarse-grained timestamps in addition to RFC3339:
  // - YYYY-MM (interpreted as the first day of month, UTC)
  // - YYYY-MM-DD (interpreted as midnight UTC)
  let s = raw;
  if (/^\d{4}-\d{2}$/.test(s)) {
    s = `${s}-01T00:00:00Z`;
  } else if (/^\d{4}-\d{2}-\d{2}$/.test(s)) {
    s = `${s}T00:00:00Z`;
  }

  const ms = Date.parse(s);
  if (!Number.isFinite(ms)) {
    throw new Error(`${fieldName} must be a timestamp string (RFC3339 or YYYY-MM[/DD])`);
  }
  return ms * 1000;
}

async function getPropertyHistory(runner, entityId, attrName, options) {
  const id = String(entityId || '').trim();
  const an = String(attrName || '').trim();
  if (!id) throw new Error('entityId is required');
  if (!an) throw new Error('attrName is required');

  const opts = options && typeof options === 'object' ? options : {};
  const fromUs = _parseTimestampToMicros(opts.from, 'from');
  const toUs = _parseTimestampToMicros(opts.to, 'to');
  if (fromUs != null && toUs != null && fromUs > toUs) {
    throw new Error('from must be <= to');
  }

  const filters = [];
  if (fromUs != null) filters.push('valid_us >= $from_us');
  if (toUs != null) filters.push('valid_us <= $to_us');

  const script = `
?[valid_us, valid_time, value, tx_time] :=
  *om_property{ entity_id: $entity_id, attr_name: $attr_name, valid_time: vld, value, tx_time },
  valid_us = to_int(vld),
  valid_time = format_timestamp(vld)${filters.length ? `,\n  ${filters.join(',\n  ')}` : ''}

:sort valid_us
  `.trim();

  const rows = await runRows(runner, script, {
    entity_id: id,
    attr_name: an,
    ...(fromUs != null ? { from_us: fromUs } : {}),
    ...(toUs != null ? { to_us: toUs } : {}),
  });

  return rows.map(([_validUs, validTime, value, txTime]) => ({
    value,
    valid_time: validTime,
    tx_time: txTime,
  }));
}

function _normalizeAsOfTimestamp(value, fieldName) {
  const raw = String(value || '').trim();
  if (!raw) {
    throw new Error(`${fieldName} is required`);
  }

  // Allow coarse-grained inputs in addition to RFC3339.
  let s = raw;
  if (/^\d{4}-\d{2}$/.test(s)) {
    s = `${s}-01T00:00:00Z`;
  } else if (/^\d{4}-\d{2}-\d{2}$/.test(s)) {
    s = `${s}T00:00:00Z`;
  }

  const ms = Date.parse(s);
  if (!Number.isFinite(ms)) {
    throw new Error(`${fieldName} must be a timestamp string (RFC3339 or YYYY-MM[/DD])`);
  }
  return new Date(ms).toISOString();
}

async function getPropertyAsOf(runner, entityId, attrName, timestamp) {
  const id = String(entityId || '').trim();
  const an = String(attrName || '').trim();
  if (!id) throw new Error('entityId is required');
  if (!an) throw new Error('attrName is required');
  const asOf = _normalizeAsOfTimestamp(timestamp, 'timestamp');

  const typeName = await getEntityType(runner, id);
  await _preloadAttrAliasesForType(runner, typeName);
  const canonicalAttrName = await _resolveAttrForCanonicalType(runner, typeName, an);

  const rows = await runRows(
    runner,
    `
?[value] :=
  *om_property{ entity_id: $entity_id, attr_name: $attr_name, value @ $as_of }
:limit 1
    `.trim(),
    { entity_id: id, attr_name: canonicalAttrName, as_of: asOf }
  );
  if (rows.length) {
    return rows[0][0];
  }

  const aliases = await _listAttrAliasesForCanonical(runner, typeName, canonicalAttrName);
  for (const aliasName of aliases) {
    const hit = await runRows(
      runner,
      `
?[value] :=
  *om_property{ entity_id: $entity_id, attr_name: $attr_name, value @ $as_of }
:limit 1
      `.trim(),
      { entity_id: id, attr_name: aliasName, as_of: asOf }
    );
    if (hit.length) {
      return hit[0][0];
    }
  }

  // Fallback: computed properties are resolved lazily and are not stored.
  const def = await _resolveComputedDef(runner, typeName, canonicalAttrName).catch(() => null);
  const defRaw = !def && an && an !== canonicalAttrName
    ? await _resolveComputedDef(runner, typeName, an).catch(() => null)
    : null;
  const chosen = def || defRaw;
  if (!chosen || typeof chosen.computeFn !== 'function') {
    return undefined;
  }

  const ctx = {
    runner,
    entityId: id,
    typeName,
    asOf,
    getProperty: async (name) => getPropertyAsOf(runner, id, name, asOf),
    // NOTE: edges are not yet as-of aware until getNeighborsAsOf lands (T2.4).
    getNeighbors: async (relName, direction) => getNeighbors(runner, id, relName, direction),
  };
  return chosen.computeFn(ctx);
}

async function validateRelation(runner, fromId, relName, toId) {
  const canonicalRelName = await resolveRel(runner, relName);
  const fromType = await getEntityType(runner, fromId);
  const toType = await getEntityType(runner, toId);
  const rows = await runDslRows(
    runner,
    query()
      .select(['from_type', 'to_type', 'directed'])
      .fromStored('om_rel_def', {
        rel_name: param('rel_name', canonicalRelName),
        from_type: dsl.var('from_type'),
        to_type: dsl.var('to_type'),
        directed: dsl.var('directed'),
      })
      .limit(1)
  );
  if (!rows.length) {
    throw new Error(`Relation '${canonicalRelName}' is not defined`);
  }
  const [expectedFromTypeRaw, expectedToTypeRaw, directedRaw] = rows[0];
  const expectedFromType = await resolveType(runner, expectedFromTypeRaw);
  const expectedToType = await resolveType(runner, expectedToTypeRaw);
  const fromOk = await isSubtypeOf(runner, fromType, expectedFromType);
  const toOk = await isSubtypeOf(runner, toType, expectedToType);
  const reverseOk = !directedRaw &&
    await isSubtypeOf(runner, fromType, expectedToType) &&
    await isSubtypeOf(runner, toType, expectedFromType);
  if ((!fromOk || !toOk) && !reverseOk) {
    throw new Error(
      `Relation '${canonicalRelName}' expects ${expectedFromType} -> ${expectedToType}, got ${fromType} -> ${toType}`
    );
  }
}

async function linkEntities(runner, fromId, relName, toId, props = {}, options) {
  // Backward compatible overload:
  // - linkEntities(runner, fromId, relName, toId, props)
  // - linkEntities(runner, fromId, relName, toId, options)  (when caller doesn't use props)
  let realProps = props;
  let opts = options;
  if (opts === undefined && realProps && typeof realProps === 'object' && !Array.isArray(realProps)) {
    const keys = Object.keys(realProps);
    const optionKeys = new Set(['skipConstraints', 'validTime']);
    const isOptionsOnly = keys.length > 0 && keys.every((k) => optionKeys.has(k));
    if (isOptionsOnly) {
      opts = realProps;
      realProps = {};
    }
  }

  const optionsObj = opts && typeof opts === 'object' ? opts : {};
  const skipConstraints = optionsObj.skipConstraints === true;
  const requestedValidTime = Object.prototype.hasOwnProperty.call(optionsObj, 'validTime')
    ? String(optionsObj.validTime || '').trim()
    : '';

  const txTime = new Date().toISOString();
  const validTime = requestedValidTime ? requestedValidTime : 'ASSERT';

  await _withWriteTxIfPossible(runner, async (txRunner) => {
    const canonicalRelName = await resolveRel(txRunner, relName);
    await validateRelation(txRunner, fromId, canonicalRelName, toId);
    await runDslRows(
      txRunner,
      query()
        .input({
          from_id: param('from_id', fromId),
          rel_name: param('rel_name', canonicalRelName),
          to_id: param('to_id', toId),
          valid_time: param('valid_time', validTime),
          props: param('props', realProps || {}),
          tx_time: param('tx_time', txTime),
        })
        .put('om_edge', ['from_id', 'rel_name', 'to_id', 'valid_time'], ['props', 'tx_time'])
    );

    if (!skipConstraints) {
      const result = await validateConstraints(txRunner, fromId, { types: ['cross-entity'] });
      if (!result.valid) {
        throw new Error(result.errors.join('; '));
      }
    }
  });
}

async function unlinkEntities(runner, fromId, relName, toId, options) {
  const opts = options && typeof options === 'object' ? options : {};
  const skipConstraints = opts.skipConstraints === true;
  const requestedValidTime = Object.prototype.hasOwnProperty.call(opts, 'validTime')
    ? String(opts.validTime || '').trim()
    : '';

  const txTime = new Date().toISOString();
  let retractValidTime;
  if (!requestedValidTime) {
    retractValidTime = 'RETRACT';
  } else if (requestedValidTime === 'ASSERT' || requestedValidTime === 'RETRACT') {
    retractValidTime = 'RETRACT';
  } else {
    retractValidTime = requestedValidTime.startsWith('~') ? requestedValidTime : `~${requestedValidTime}`;
  }

  await _withWriteTxIfPossible(runner, async (txRunner) => {
    const canonicalRelName = await resolveRel(txRunner, relName);
    await validateRelation(txRunner, fromId, canonicalRelName, toId);
    await runDslRows(
      txRunner,
      query()
        .input({
          from_id: param('from_id', fromId),
          rel_name: param('rel_name', canonicalRelName),
          to_id: param('to_id', toId),
          valid_time: param('valid_time', retractValidTime),
          props: param('props', {}),
          tx_time: param('tx_time', txTime),
        })
        .put('om_edge', ['from_id', 'rel_name', 'to_id', 'valid_time'], ['props', 'tx_time'])
    );

    if (!skipConstraints) {
      const result = await validateConstraints(txRunner, fromId, { types: ['cross-entity'] });
      if (!result.valid) {
        throw new Error(result.errors.join('; '));
      }
    }
  });
}

async function getAllProperties(runner, entityId) {
  const rows = await runRows(
    runner,
    `
?[attr_name, value] :=
  *om_property{ entity_id: $entity_id, attr_name, value @ "NOW" }
    `.trim(),
    { entity_id: entityId }
  );
  return Object.fromEntries(rows.map(([name, value]) => [name, value]));
}

async function _canonicalizeStoredPropertiesForType(runner, canonicalTypeName, rawProperties) {
  const tn = String(canonicalTypeName || '').trim();
  if (!tn) throw new Error('Type name is required');
  const raw = rawProperties && typeof rawProperties === 'object' ? rawProperties : {};

  await _preloadAttrAliasesForType(runner, tn);

  const out = {};
  const sourceIsCanonical = new Map();
  for (const [storedAttrName, value] of Object.entries(raw)) {
    const stored = String(storedAttrName || '').trim();
    if (!stored) continue;
    const canonicalAttrName = await _resolveAttrForCanonicalType(runner, tn, stored);
    const isCanonSource = stored === canonicalAttrName;
    if (!Object.prototype.hasOwnProperty.call(out, canonicalAttrName)) {
      out[canonicalAttrName] = value;
      sourceIsCanonical.set(canonicalAttrName, isCanonSource);
      continue;
    }
    const existingIsCanonical = sourceIsCanonical.get(canonicalAttrName) === true;
    if (!existingIsCanonical && isCanonSource) {
      out[canonicalAttrName] = value;
      sourceIsCanonical.set(canonicalAttrName, true);
    }
  }
  return out;
}

async function validateRequiredProperties(runner, entityId) {
  const typeName = await getEntityType(runner, entityId);
  const definitions = await getAttributeDefinitions(runner, typeName);
  const rawProperties = await getAllProperties(runner, entityId);
  const properties = await _canonicalizeStoredPropertiesForType(runner, typeName, rawProperties);
  const missing = [];
  for (const [attrName, def] of definitions.entries()) {
    if (def.required && !(attrName in properties)) {
      missing.push(attrName);
    }
  }
  return missing;
}

async function validateEntity(runner, entityId) {
  const errors = [];
  const typeName = await getEntityType(runner, entityId);
  const definitions = await getAttributeDefinitions(runner, typeName);
  const rawProperties = await getAllProperties(runner, entityId);
  const properties = await _canonicalizeStoredPropertiesForType(runner, typeName, rawProperties);

  for (const [attrName, def] of definitions.entries()) {
    if (def.required && !(attrName in properties)) {
      errors.push(`Missing required property '${attrName}'`);
    }
  }

  for (const [attrName, value] of Object.entries(properties)) {
    const definition = definitions.get(attrName);
    if (!definition) {
      errors.push(`Undefined property '${attrName}' for type '${typeName}'`);
      continue;
    }
    const actualType = inferValueType(value);
    if (actualType !== definition.valueType) {
      errors.push(
        `Property '${attrName}' expects ${definition.valueType}, got ${actualType}`
      );
    }
  }

  return { valid: errors.length === 0, errors };
}

async function finalizeEntity(runner, entityId) {
  const result = await validateEntity(runner, entityId);
  if (!result.valid) {
    throw new Error(`Entity '${entityId}' validation failed: ${result.errors.join('; ')}`);
  }
}

async function getEntityView(runner, entityId) {
  const entityRows = await runDslRows(
    runner,
    query()
      .select(['type_name', 'label'])
      .fromStored('om_entity', {
        id: param('id', entityId),
        type_name: dsl.var('type_name'),
        label: dsl.var('label'),
      })
      .limit(1)
  );
  if (!entityRows.length) {
    return null;
  }
  const storedTypeName = entityRows[0][0];
  const label = entityRows[0][1];
  const typeName = await resolveType(runner, storedTypeName);

  const rawProperties = await getAllProperties(runner, entityId);
  const properties = await _canonicalizeStoredPropertiesForType(runner, typeName, rawProperties);

  // Append computed properties (lazy, not stored)
  const computed = await _getComputedMapForType(runner, typeName);
  for (const [attrName] of computed.entries()) {
    const canonicalAttrName = await _resolveAttrForCanonicalType(runner, typeName, attrName);
    if (canonicalAttrName in properties) continue;
    try {
      const v = await getProperty(runner, entityId, canonicalAttrName);
      if (v !== undefined) {
        properties[canonicalAttrName] = v;
      }
    } catch (_) {
      // Ignore computation failures in view assembly (caller can request explicit evaluation later)
    }
  }

  const outgoingNeighbors = await getNeighbors(runner, entityId, null, 'outgoing');
  const outgoing = outgoingNeighbors.outgoing.map((entry) => ({
    relName: entry.relName,
    toId: entry.entityId,
    toType: entry.typeName,
    toLabel: entry.label,
  }));
  return { id: entityId, typeName, label, properties, outgoing };
}

async function getEntityViewAsOf(runner, entityId, timestamp) {
  const id = String(entityId || '').trim();
  if (!id) throw new Error('entityId is required');
  const asOf = _normalizeAsOfTimestamp(timestamp, 'timestamp');

  const entityRows = await runDslRows(
    runner,
    query()
      .select(['type_name', 'label'])
      .fromStored('om_entity', {
        id: param('id', id),
        type_name: dsl.var('type_name'),
        label: dsl.var('label'),
      })
      .limit(1)
  );
  if (!entityRows.length) {
    return null;
  }

  const storedTypeName = entityRows[0][0];
  const label = entityRows[0][1];
  const typeName = await resolveType(runner, storedTypeName);

  const propRows = await runRows(
    runner,
    `
?[attr_name, value] :=
  *om_property{ entity_id: $entity_id, attr_name, value @ $as_of }
    `.trim(),
    { entity_id: id, as_of: asOf }
  );
  const rawProperties = Object.fromEntries(propRows.map(([name, value]) => [name, value]));
  const properties = await _canonicalizeStoredPropertiesForType(runner, typeName, rawProperties);

  // Append computed properties (lazy, not stored) evaluated against asOf context.
  const computed = await _getComputedMapForType(runner, typeName);
  for (const [attrName] of computed.entries()) {
    const canonicalAttrName = await _resolveAttrForCanonicalType(runner, typeName, attrName);
    if (canonicalAttrName in properties) continue;
    try {
      const v = await getPropertyAsOf(runner, id, canonicalAttrName, asOf);
      if (v !== undefined) {
        properties[canonicalAttrName] = v;
      }
    } catch (_) {
    }
  }

  const neighbors = await getNeighborsAsOf(runner, id, null, asOf);
  const outgoing = neighbors.outgoing.map((entry) => ({
    relName: entry.relName,
    toId: entry.entityId,
    toType: entry.typeName,
    toLabel: entry.label,
  }));

  return { id, typeName, label, properties, outgoing };
}

async function getNeighbors(runner, entityId, relName, direction) {
  const dir = direction == null || direction === '' ? 'both' : normalizeDirection(direction);
  const wantOutgoing = dir === 'outgoing' || dir === 'both';
  const wantIncoming = dir === 'incoming' || dir === 'both';
  let outgoingRows;
  let incomingRows;

  if (relName) {
    const canonicalRelName = await resolveRel(runner, relName);
    await _preloadRelAliases(runner);

    const relNamesToCheck = [canonicalRelName, ...(await _listRelAliasesForCanonical(runner, canonicalRelName))]
      .filter(Boolean);

    const outgoing = [];
    const incoming = [];
    const seenOut = new Set();
    const seenIn = new Set();

    for (const rn of relNamesToCheck) {
      if (wantOutgoing) {
        const rows = await runRows(
          runner,
          `
?[rel_name, node_id, node_type, node_label] :=
  *om_edge{ from_id: $id, rel_name, to_id: node_id, props: _props @ "NOW" },
  *om_entity{ id: node_id, type_name: node_type, label: node_label },
  rel_name = $rel_name
          `.trim(),
          { id: entityId, rel_name: rn }
        );
        for (const [_edgeRelName, nodeId, nodeType, nodeLabel] of rows) {
          const key = `${canonicalRelName}|${nodeId}`;
          if (seenOut.has(key)) continue;
          seenOut.add(key);
          outgoing.push({ relName: canonicalRelName, entityId: nodeId, typeName: await resolveType(runner, nodeType), label: nodeLabel });
        }
      }

      if (wantIncoming) {
        const rows = await runRows(
          runner,
          `
?[rel_name, node_id, node_type, node_label] :=
  *om_edge{ from_id: node_id, rel_name, to_id: $id, props: _props @ "NOW" },
  *om_entity{ id: node_id, type_name: node_type, label: node_label },
  rel_name = $rel_name
          `.trim(),
          { id: entityId, rel_name: rn }
        );
        for (const [_edgeRelName, nodeId, nodeType, nodeLabel] of rows) {
          const key = `${canonicalRelName}|${nodeId}`;
          if (seenIn.has(key)) continue;
          seenIn.add(key);
          incoming.push({ relName: canonicalRelName, entityId: nodeId, typeName: await resolveType(runner, nodeType), label: nodeLabel });
        }
      }
    }

    return { outgoing, incoming };
  }

  outgoingRows = wantOutgoing
    ? await runRows(
      runner,
      `
?[rel_name, node_id, node_type, node_label] :=
  *om_edge{ from_id: $id, rel_name, to_id: node_id, props: _props @ "NOW" },
  *om_entity{ id: node_id, type_name: node_type, label: node_label }
      `.trim(),
      { id: entityId }
    )
    : [];
  incomingRows = wantIncoming
    ? await runRows(
      runner,
      `
?[rel_name, node_id, node_type, node_label] :=
  *om_edge{ from_id: node_id, rel_name, to_id: $id, props: _props @ "NOW" },
  *om_entity{ id: node_id, type_name: node_type, label: node_label }
      `.trim(),
      { id: entityId }
    )
    : [];

  await _preloadRelAliases(runner);
  await _preloadTypeAliases(runner);

  const relCanonCache = new Map();
  const typeCanonCache = new Map();
  async function canonRel(name) {
    const n = String(name || '').trim();
    if (!n) return '';
    if (relCanonCache.has(n)) return relCanonCache.get(n);
    const c = await resolveRel(runner, n);
    relCanonCache.set(n, c);
    return c;
  }
  async function canonType(name) {
    const n = String(name || '').trim();
    if (!n) return '';
    if (typeCanonCache.has(n)) return typeCanonCache.get(n);
    const c = await resolveType(runner, n);
    typeCanonCache.set(n, c);
    return c;
  }

  const outgoing = [];
  for (const [edgeRelName, nodeId, nodeType, nodeLabel] of outgoingRows) {
    outgoing.push({
      relName: await canonRel(edgeRelName),
      entityId: nodeId,
      typeName: await canonType(nodeType),
      label: nodeLabel,
    });
  }
  const incoming = [];
  for (const [edgeRelName, nodeId, nodeType, nodeLabel] of incomingRows) {
    incoming.push({
      relName: await canonRel(edgeRelName),
      entityId: nodeId,
      typeName: await canonType(nodeType),
      label: nodeLabel,
    });
  }

  return { outgoing, incoming };
}

async function getNeighborsAsOf(runner, entityId, relName, timestamp) {
  const id = String(entityId || '').trim();
  if (!id) throw new Error('entityId is required');
  const asOf = _normalizeAsOfTimestamp(timestamp, 'timestamp');

  const rnInput = relName != null ? String(relName).trim() : '';
  const hasRelName = !!rnInput;

  let outgoingRows;
  let incomingRows;
  if (hasRelName) {
    const canonicalRelName = await resolveRel(runner, rnInput);
    await _preloadRelAliases(runner);
    const relNamesToCheck = [canonicalRelName, ...(await _listRelAliasesForCanonical(runner, canonicalRelName))]
      .filter(Boolean);

    const outgoing = [];
    const incoming = [];
    const seenOut = new Set();
    const seenIn = new Set();

    for (const rn of relNamesToCheck) {
      outgoingRows = await runRows(
        runner,
        `
?[rel_name, node_id, node_type, node_label] :=
  *om_edge{ from_id: $id, rel_name, to_id: node_id, props: _props @ $as_of },
  *om_entity{ id: node_id, type_name: node_type, label: node_label },
  rel_name = $rel_name
        `.trim(),
        { id, rel_name: rn, as_of: asOf }
      );
      for (const [_edgeRelName, nodeId, nodeType, nodeLabel] of outgoingRows) {
        const key = `${canonicalRelName}|${nodeId}`;
        if (seenOut.has(key)) continue;
        seenOut.add(key);
        outgoing.push({ relName: canonicalRelName, entityId: nodeId, typeName: await resolveType(runner, nodeType), label: nodeLabel });
      }

      incomingRows = await runRows(
        runner,
        `
?[rel_name, node_id, node_type, node_label] :=
  *om_edge{ from_id: node_id, rel_name, to_id: $id, props: _props @ $as_of },
  *om_entity{ id: node_id, type_name: node_type, label: node_label },
  rel_name = $rel_name
        `.trim(),
        { id, rel_name: rn, as_of: asOf }
      );
      for (const [_edgeRelName, nodeId, nodeType, nodeLabel] of incomingRows) {
        const key = `${canonicalRelName}|${nodeId}`;
        if (seenIn.has(key)) continue;
        seenIn.add(key);
        incoming.push({ relName: canonicalRelName, entityId: nodeId, typeName: await resolveType(runner, nodeType), label: nodeLabel });
      }
    }

    return { outgoing, incoming };
  }

  outgoingRows = await runRows(
    runner,
    `
?[rel_name, node_id, node_type, node_label] :=
  *om_edge{ from_id: $id, rel_name, to_id: node_id, props: _props @ $as_of },
  *om_entity{ id: node_id, type_name: node_type, label: node_label }
    `.trim(),
    { id, as_of: asOf }
  );
  incomingRows = await runRows(
    runner,
    `
?[rel_name, node_id, node_type, node_label] :=
  *om_edge{ from_id: node_id, rel_name, to_id: $id, props: _props @ $as_of },
  *om_entity{ id: node_id, type_name: node_type, label: node_label }
    `.trim(),
    { id, as_of: asOf }
  );

  await _preloadRelAliases(runner);
  await _preloadTypeAliases(runner);

  const relCanonCache = new Map();
  const typeCanonCache = new Map();
  async function canonRel(name) {
    const n = String(name || '').trim();
    if (!n) return '';
    if (relCanonCache.has(n)) return relCanonCache.get(n);
    const c = await resolveRel(runner, n);
    relCanonCache.set(n, c);
    return c;
  }
  async function canonType(name) {
    const n = String(name || '').trim();
    if (!n) return '';
    if (typeCanonCache.has(n)) return typeCanonCache.get(n);
    const c = await resolveType(runner, n);
    typeCanonCache.set(n, c);
    return c;
  }

  return {
    outgoing: await Promise.all(outgoingRows.map(async ([edgeRelName, nodeId, nodeType, nodeLabel]) => ({
      relName: await canonRel(edgeRelName),
      entityId: nodeId,
      typeName: await canonType(nodeType),
      label: nodeLabel,
    }))),
    incoming: await Promise.all(incomingRows.map(async ([edgeRelName, nodeId, nodeType, nodeLabel]) => ({
      relName: await canonRel(edgeRelName),
      entityId: nodeId,
      typeName: await canonType(nodeType),
      label: nodeLabel,
    }))),
  };
}

async function getEdgeHistory(runner, fromId, relName, toId, options) {
  const fid = String(fromId || '').trim();
  const rn = String(relName || '').trim();
  if (!fid) throw new Error('fromId is required');
  if (!rn) throw new Error('relName is required');

  // Backward compatible overload:
  // - getEdgeHistory(runner, fromId, relName)
  // - getEdgeHistory(runner, fromId, relName, toId)
  // - getEdgeHistory(runner, fromId, relName, options)
  // - getEdgeHistory(runner, fromId, relName, toId, options)
  let realToId = toId;
  let opts = options;
  if (opts === undefined && realToId && typeof realToId === 'object' && !Array.isArray(realToId)) {
    opts = realToId;
    realToId = undefined;
  }

  const toIdStr = realToId == null ? '' : String(realToId).trim();
  const hasToId = !!toIdStr;

  const optionsObj = opts && typeof opts === 'object' ? opts : {};
  const fromUs = _parseTimestampToMicros(optionsObj.from, 'from');
  const toUs = _parseTimestampToMicros(optionsObj.to, 'to');
  if (fromUs != null && toUs != null && fromUs > toUs) {
    throw new Error('from must be <= to');
  }

  const filters = [];
  if (hasToId) filters.push('to_id = $to_id');
  if (fromUs != null) filters.push('valid_us >= $from_us');
  if (toUs != null) filters.push('valid_us <= $to_us');

  const script = `
?[valid_us, valid_time, to_id, is_assert, props, tx_time] :=
  *om_edge{ from_id: $from_id, rel_name: $rel_name, to_id, valid_time: vld, props, tx_time },
  valid_us = to_int(vld),
  valid_time = format_timestamp(vld),
  is_assert = to_bool(vld)${filters.length ? `,\n  ${filters.join(',\n  ')}` : ''}

:sort valid_us, to_id, is_assert
  `.trim();

  const rows = await runRows(runner, script, {
    from_id: fid,
    rel_name: rn,
    ...(hasToId ? { to_id: toIdStr } : {}),
    ...(fromUs != null ? { from_us: fromUs } : {}),
    ...(toUs != null ? { to_us: toUs } : {}),
  });

  return rows.map(([_validUs, validTime, toIdOut, isAssert, props, txTime]) => ({
    fromId: fid,
    relName: rn,
    toId: toIdOut,
    props,
    valid_time: validTime,
    tx_time: txTime,
    is_assert: isAssert,
  }));
}

async function traverse(runner, startId, relPath) {
  if (!Array.isArray(relPath) || relPath.length === 0) {
    const view = await getEntityView(runner, startId);
    return view ? [{ id: view.id, typeName: view.typeName, label: view.label }] : [];
  }

  let frontier = [startId];
  let levelNodes = [];

  for (const relName of relPath) {
    const nextMap = new Map();
    for (const fromId of frontier) {
      const rows = await runRows(
        runner,
        `
?[id, type_name, label] :=
  *om_edge{ from_id: $from_id, rel_name: $rel_name, to_id: id, props: _props @ "NOW" },
  *om_entity{ id, type_name, label }
        `.trim(),
        { from_id: fromId, rel_name: relName }
      );
      for (const [id, typeName, label] of rows) {
        nextMap.set(id, { id, typeName, label });
      }
    }
    levelNodes = [...nextMap.values()];
    frontier = levelNodes.map((node) => node.id);
    if (!frontier.length) {
      break;
    }
  }

  return levelNodes;
}

async function findByType(runner, typeName, filter, options) {
  const filterObj = filter || {};
  const opts = options || {};
  const exact = !!opts.exact;

  let typeNames = [typeName];
  if (!exact) {
    const descendants = await getDescendants(runner, typeName);
    typeNames = typeNames.concat(descendants);
  }

  const allEntries = [];
  for (const tn of typeNames) {
    const rows = await runDslRows(
      runner,
      query()
        .select(['id', 'label'])
        .fromStored('om_entity', {
          id: dsl.var('id'),
          type_name: param('type_name', tn),
          label: dsl.var('label'),
        })
        .order('id')
    );

    for (const [id, label] of rows) {
      const properties = await getAllProperties(runner, id);
      let matched = true;
      for (const [key, expected] of Object.entries(filterObj)) {
        if (!Object.is(properties[key], expected)) {
          matched = false;
          break;
        }
      }
      if (matched) {
        allEntries.push({ id, label, properties });
      }
    }
  }
  return allEntries;
}

async function aggregateByType(runner, typeName, attrName, op, options) {
  const normalizedOp = String(op || '').toLowerCase();
  const allowedOps = new Set(['sum', 'avg', 'min', 'max', 'count']);
  if (!allowedOps.has(normalizedOp)) {
    throw new Error(`Unsupported aggregate op '${op}'`);
  }

  const opts = options || {};
  const exact = !!opts.exact;

  let typeNames = [typeName];
  if (!exact) {
    const descendants = await getDescendants(runner, typeName);
    typeNames = typeNames.concat(descendants);
  }

  const allRows = [];
  for (const tn of typeNames) {
    const rows = await runRows(
      runner,
      `
?[value] :=
  *om_entity{ id, type_name: $type_name, label: _label },
  *om_property{ entity_id: id, attr_name: $attr_name, value @ "NOW" }
      `.trim(),
      { type_name: tn, attr_name: attrName }
    );
    allRows.push(...rows);
  }

  if (normalizedOp === 'count') {
    return allRows.length;
  }

  const values = allRows.map(([value]) => value).filter((value) => typeof value === 'number' && Number.isFinite(value));
  if (!values.length) {
    return 0;
  }

  if (normalizedOp === 'sum') {
    return values.reduce((acc, value) => acc + value, 0);
  }
  if (normalizedOp === 'avg') {
    return values.reduce((acc, value) => acc + value, 0) / values.length;
  }
  if (normalizedOp === 'min') {
    return Math.min(...values);
  }
  return Math.max(...values);
}

function normalizeDirection(direction) {
  const normalized = String(direction || 'outgoing').toLowerCase();
  if (!['outgoing', 'incoming', 'both'].includes(normalized)) {
    throw new Error(`Unsupported direction '${direction}'`);
  }
  return normalized;
}

function countByType(nodes) {
  const counts = {};
  for (const node of nodes) {
    counts[node.typeName] = (counts[node.typeName] || 0) + 1;
  }
  return counts;
}

function buildGraphVisual(nodes, edges, options = {}) {
  const rootId = options.rootId;
  const nodeMap = {};
  const adjacency = {};

  const visualNodes = nodes.map((node) => {
    const formatted = {
      id: node.id,
      label: node.label,
      kind: node.typeName,
      group: node.typeName,
      depth: Number.isInteger(node.depth) ? node.depth : 0,
      metrics: {},
      flags: { isRoot: rootId === node.id },
    };
    nodeMap[node.id] = formatted;
    if (!adjacency[node.id]) {
      adjacency[node.id] = [];
    }
    return formatted;
  });

  const visualEdges = edges.map((edge, index) => {
    const formatted = {
      id: `e:${index}:${edge.fromId}:${edge.relName}:${edge.toId}`,
      source: edge.fromId,
      target: edge.toId,
      kind: edge.relName,
      label: edge.relName,
      direction: edge.direction || 'outgoing',
      weight: 1,
      flags: {},
    };
    if (!adjacency[edge.fromId]) {
      adjacency[edge.fromId] = [];
    }
    adjacency[edge.fromId].push({
      toId: edge.toId,
      relName: edge.relName,
      direction: formatted.direction,
    });
    return formatted;
  });

  return {
    nodes: visualNodes,
    edges: visualEdges,
    nodeMap,
    adjacency,
  };
}

function buildTreeVisual(rootId, edges) {
  const childrenById = {};
  for (const edge of edges) {
    if (!childrenById[edge.fromId]) {
      childrenById[edge.fromId] = [];
    }
    childrenById[edge.fromId].push({
      toId: edge.toId,
      relName: edge.relName,
      direction: edge.direction,
    });
  }
  return {
    rootId,
    childrenById,
    crossEdges: [],
  };
}

function buildRankingVisual(hotspots) {
  const ranking = hotspots.map((hotspot) => ({
    rank: hotspot.rank,
    id: hotspot.entity.id,
    label: hotspot.entity.label,
    score: hotspot.score,
    factors: hotspot.factors,
  }));
  return {
    ranking,
    series: {
      labels: ranking.map((entry) => entry.label),
      values: ranking.map((entry) => entry.score),
    },
  };
}

async function walkImpactGraph(runner, {
  rootId,
  relNames,
  maxDepth,
  direction,
}) {
  const rootView = await getEntityView(runner, rootId);
  if (!rootView) {
    throw new Error(`Root entity '${rootId}' does not exist`);
  }

  const normalizedDirection = normalizeDirection(direction);
  const relationFilters = Array.isArray(relNames) ? relNames.filter(Boolean) : [];
  const queue = [{ id: rootId, depth: 0 }];
  const visited = new Set([rootId]);
  const nodes = new Map([[rootId, {
    id: rootId,
    typeName: rootView.typeName,
    label: rootView.label,
    depth: 0,
  }]]);
  const edges = new Map();
  let maxDepthReached = 0;
  let cycleDetected = false;

  while (queue.length) {
    const current = queue.shift();
    if (current.depth >= maxDepth) {
      continue;
    }

    const neighborDatasets = [];
    if (relationFilters.length) {
      for (const relName of relationFilters) {
        neighborDatasets.push(await getNeighbors(runner, current.id, relName));
      }
    } else {
      neighborDatasets.push(await getNeighbors(runner, current.id));
    }

    for (const neighbors of neighborDatasets) {
      if (normalizedDirection === 'outgoing' || normalizedDirection === 'both') {
        for (const entry of neighbors.outgoing) {
          const edgeKey = `${current.id}|${entry.relName}|${entry.entityId}`;
          edges.set(edgeKey, {
            fromId: current.id,
            toId: entry.entityId,
            relName: entry.relName,
            direction: 'outgoing',
          });

          if (!nodes.has(entry.entityId)) {
            nodes.set(entry.entityId, {
              id: entry.entityId,
              typeName: entry.typeName,
              label: entry.label,
              depth: current.depth + 1,
            });
          }

          if (visited.has(entry.entityId)) {
            cycleDetected = true;
          } else {
            visited.add(entry.entityId);
            queue.push({ id: entry.entityId, depth: current.depth + 1 });
            maxDepthReached = Math.max(maxDepthReached, current.depth + 1);
          }
        }
      }

      if (normalizedDirection === 'incoming' || normalizedDirection === 'both') {
        for (const entry of neighbors.incoming) {
          const edgeKey = `${entry.entityId}|${entry.relName}|${current.id}`;
          edges.set(edgeKey, {
            fromId: entry.entityId,
            toId: current.id,
            relName: entry.relName,
            direction: 'incoming',
          });

          if (!nodes.has(entry.entityId)) {
            nodes.set(entry.entityId, {
              id: entry.entityId,
              typeName: entry.typeName,
              label: entry.label,
              depth: current.depth + 1,
            });
          }

          if (visited.has(entry.entityId)) {
            cycleDetected = true;
          } else {
            visited.add(entry.entityId);
            queue.push({ id: entry.entityId, depth: current.depth + 1 });
            maxDepthReached = Math.max(maxDepthReached, current.depth + 1);
          }
        }
      }
    }
  }

  return {
    nodes: [...nodes.values()],
    edges: [...edges.values()],
    cycleDetected,
    maxDepthReached,
  };
}

async function impactAnalysis(runner, input = {}) {
  const rootId = input.rootId;
  if (!rootId) {
    throw new Error('impactAnalysis requires input.rootId');
  }

  const maxDepth = Number.isInteger(input.maxDepth) && input.maxDepth >= 0 ? input.maxDepth : 2;
  const relNames = Array.isArray(input.relNames) ? input.relNames : [];
  const direction = normalizeDirection(input.direction || 'outgoing');
  const graph = await walkImpactGraph(runner, { rootId, relNames, maxDepth, direction });
  const byType = countByType(graph.nodes);
  const visualGraph = buildGraphVisual(graph.nodes, graph.edges, { rootId });

  return {
    template: 'impactAnalysis',
    version: 'v1',
    input: { rootId, relNames, maxDepth, direction },
    data: {
      nodes: graph.nodes,
      edges: graph.edges,
      visual: {
        primary: 'graph',
        graph: visualGraph,
        legend: { byType },
      },
    },
    stats: {
      impactedCount: Math.max(0, graph.nodes.length - 1),
      byType,
      maxDepthReached: graph.maxDepthReached,
      cycleDetected: graph.cycleDetected,
      truncated: graph.maxDepthReached >= maxDepth,
    },
    warnings: [],
  };
}

async function ownershipTree(runner, input = {}) {
  const rootId = input.rootId;
  if (!rootId) {
    throw new Error('ownershipTree requires input.rootId');
  }

  const ownerRelNames = Array.isArray(input.ownerRelNames) && input.ownerRelNames.length
    ? input.ownerRelNames
    : ['owns', 'contains'];
  const maxDepth = Number.isInteger(input.maxDepth) && input.maxDepth >= 0 ? input.maxDepth : 3;

  const graph = await walkImpactGraph(runner, {
    rootId,
    relNames: ownerRelNames,
    maxDepth,
    direction: 'outgoing',
  });
  const visualGraph = buildGraphVisual(graph.nodes, graph.edges, { rootId });
  const visualTree = buildTreeVisual(rootId, graph.edges);

  return {
    template: 'ownershipTree',
    version: 'v1',
    input: { rootId, ownerRelNames, maxDepth },
    data: {
      rootId,
      nodes: graph.nodes,
      edges: graph.edges,
      visual: {
        primary: 'tree',
        tree: visualTree,
        graph: visualGraph,
      },
    },
    stats: {
      nodeCount: graph.nodes.length,
      edgeCount: graph.edges.length,
      maxDepthReached: graph.maxDepthReached,
      cycleDetected: graph.cycleDetected,
      truncated: graph.maxDepthReached >= maxDepth,
    },
    warnings: [],
  };
}

async function riskHotspot(runner, input = {}) {
  const typeName = input.typeName || 'Task';
  const riskAttr = input.riskAttr || 'estimate_hours';
  const minScore = typeof input.minScore === 'number' ? input.minScore : 0;
  const topK = Number.isInteger(input.topK) && input.topK > 0 ? input.topK : 5;
  const degreeWeight = typeof input.degreeWeight === 'number' ? input.degreeWeight : 1;

  const entities = await findByType(runner, typeName);
  const scored = [];

  for (const entity of entities) {
    const baseScore = entity.properties[riskAttr];
    if (typeof baseScore !== 'number' || !Number.isFinite(baseScore)) {
      continue;
    }

    const neighbors = await getNeighbors(runner, entity.id);
    const degree = neighbors.incoming.length + neighbors.outgoing.length;
    const score = baseScore + degreeWeight * degree;
    if (score < minScore) {
      continue;
    }

    scored.push({
      entity: {
        id: entity.id,
        label: entity.label,
        typeName,
      },
      score,
      factors: {
        baseScore,
        degree,
        degreeWeight,
      },
    });
  }

  scored.sort((left, right) => right.score - left.score || left.entity.id.localeCompare(right.entity.id));
  const hotspots = scored.slice(0, topK).map((entry, index) => ({
    rank: index + 1,
    ...entry,
  }));
  const visual = buildRankingVisual(hotspots);

  return {
    template: 'riskHotspot',
    version: 'v1',
    input: { typeName, riskAttr, minScore, topK, degreeWeight },
    data: {
      hotspots,
      visual: {
        primary: 'ranking',
        ranking: visual.ranking,
        series: visual.series,
      },
    },
    stats: {
      evaluatedCount: entities.length,
      returnedCount: hotspots.length,
    },
    warnings: [],
  };
}

async function ingestBatch(db, batch, options = {}) {
  if (!db || typeof db.multiTransact !== 'function') {
    throw new Error('ingestBatch requires a CozoDb instance with multiTransact(write)');
  }

  const entities = Array.isArray(batch && batch.entities) ? batch.entities : [];
  const properties = Array.isArray(batch && batch.properties) ? batch.properties : [];
  const edges = Array.isArray(batch && batch.edges) ? batch.edges : [];
  const touchedEntityIds = new Set();
  const tx = db.multiTransact(true);

  try {
    for (const entity of entities) {
      await createEntity(tx, entity.id, entity.typeName, entity.label);
      touchedEntityIds.add(entity.id);
    }

    for (const property of properties) {
      await setProperty(tx, property.entityId, property.attrName, property.value);
      touchedEntityIds.add(property.entityId);
    }

    for (const edge of edges) {
      await linkEntities(tx, edge.fromId, edge.relName, edge.toId, edge.props || {});
    }

    if (options.validateRequired !== false) {
      for (const entityId of touchedEntityIds) {
        await finalizeEntity(tx, entityId);
      }
    }

    tx.commit();
    return {
      entities: entities.length,
      properties: properties.length,
      edges: edges.length,
      validatedEntities: touchedEntityIds.size,
    };
  } catch (error) {
    try {
      tx.abort();
    } catch (_) {
    }
    throw error;
  }
}

// ---------------------------------------------------------------------------
// Existential rules (OM-024 ~ OM-027)
//
// Declarative "for each X there must exist an R-edge to some Y" rules
// (v1 head shape: exists { rel, direction?, toType } only — attribute
// existence stays with required + validateConstraints).
// Specs are pure JSON persisted in om_existential_rule_def so they can join
// schema snapshots/diff/rollback, unlike the in-memory JS constraint registry.
// ---------------------------------------------------------------------------

const EXISTENTIAL_RULE_MODES = new Set(['check', 'materialize']);
const EXISTENTIAL_WHERE_OPS = new Set(['=', '!=', '>', '>=', '<', '<=']);
const SKOLEM_ORIGIN_ATTR = '_skolem_rule';

function _normalizeExistentialDirection(direction) {
  const d = String(direction == null || direction === '' ? 'out' : direction).toLowerCase();
  if (d === 'out' || d === 'outgoing') return 'out';
  if (d === 'in' || d === 'incoming') return 'in';
  throw new Error(`exists.direction must be 'out' or 'in', got '${direction}'`);
}

async function _relDefExists(runner, canonicalRelName) {
  const rows = await runDslRows(
    runner,
    query()
      .select(['from_type'])
      .fromStored('om_rel_def', {
        rel_name: param('rel_name', canonicalRelName),
        from_type: dsl.var('from_type'),
        to_type: dsl.var('_to_type'),
        directed: dsl.var('_directed'),
      })
      .limit(1)
  );
  return rows.length > 0;
}

async function _normalizeExistentialSpec(runner, spec) {
  if (!spec || typeof spec !== 'object') {
    throw new Error('Existential rule spec must be an object');
  }

  const forEach = spec.forEach;
  if (!forEach || typeof forEach !== 'object') {
    throw new Error('Existential rule spec requires forEach.type');
  }
  const bodyTypeRaw = String(forEach.type || '').trim();
  if (!bodyTypeRaw) {
    throw new Error('Existential rule spec requires forEach.type');
  }

  const exists = spec.exists;
  if (!exists || typeof exists !== 'object' || !String(exists.rel || '').trim()) {
    throw new Error('Existential rule spec requires exists.rel');
  }
  if (!String(exists.toType || '').trim()) {
    throw new Error('Existential rule spec requires exists.toType');
  }

  const mode = String(spec.mode == null || spec.mode === '' ? 'check' : spec.mode).trim();
  if (!EXISTENTIAL_RULE_MODES.has(mode)) {
    throw new Error(`Existential rule mode must be one of ${[...EXISTENTIAL_RULE_MODES].join('/')}, got '${mode}'`);
  }

  const bodyType = await resolveType(runner, bodyTypeRaw);
  if (!(await _typeExists(runner, bodyType))) {
    throw new Error(`Unknown type '${bodyType}' in forEach.type`);
  }

  const relName = await resolveRel(runner, String(exists.rel).trim());
  if (!(await _relDefExists(runner, relName))) {
    throw new Error(`Unknown relation '${relName}' in exists.rel`);
  }

  const toType = await resolveType(runner, String(exists.toType).trim());
  if (!(await _typeExists(runner, toType))) {
    throw new Error(`Unknown type '${toType}' in exists.toType`);
  }

  const direction = _normalizeExistentialDirection(exists.direction);

  const where = [];
  if (forEach.where != null) {
    if (!Array.isArray(forEach.where)) {
      throw new Error('forEach.where must be an array of { attr, op, value }');
    }
    for (const cond of forEach.where) {
      if (!cond || typeof cond !== 'object') {
        throw new Error('forEach.where entries must be objects of { attr, op, value }');
      }
      const attrRaw = String(cond.attr || '').trim();
      if (!attrRaw) throw new Error('forEach.where entries require attr');
      const op = String(cond.op || '=').trim();
      if (!EXISTENTIAL_WHERE_OPS.has(op)) {
        throw new Error(`forEach.where op must be one of ${[...EXISTENTIAL_WHERE_OPS].join(' ')}, got '${op}'`);
      }
      // undefined would be dropped by JSON persistence, leaving a stored
      // condition that silently never matches; require an explicit value
      // (null is a legal JSON value and stays allowed).
      if (!Object.prototype.hasOwnProperty.call(cond, 'value') || cond.value === undefined) {
        throw new Error(`forEach.where entries require an explicit value (attr '${attrRaw}'); use null for a null comparison`);
      }
      const attr = await _resolveAttrForCanonicalType(runner, bodyType, attrRaw);
      where.push({ attr, op, value: cond.value });
    }
  }

  const normalized = {
    forEach: { type: bodyType },
    exists: { rel: relName, direction, toType },
  };
  if (where.length) normalized.forEach.where = where;

  if (spec.materialize != null) {
    if (typeof spec.materialize !== 'object') {
      throw new Error('materialize must be an object');
    }
    const mat = {};
    if (spec.materialize.labelTemplate != null) {
      mat.labelTemplate = String(spec.materialize.labelTemplate);
    }
    if (spec.materialize.props != null) {
      if (typeof spec.materialize.props !== 'object' || Array.isArray(spec.materialize.props)) {
        throw new Error('materialize.props must be an object');
      }
      mat.props = spec.materialize.props;
    }
    normalized.materialize = mat;
  }

  return { normalized, mode };
}

async function defineExistentialRule(runner, ruleName, spec) {
  const rn = String(ruleName || '').trim();
  if (!rn) throw new Error('Rule name is required');

  const { normalized, mode } = await _normalizeExistentialSpec(runner, spec);
  const message = spec.message != null ? String(spec.message) : '';
  const enabled = spec.enabled !== false;

  if (mode === 'materialize') {
    // Skolem entities are marked via a real attribute definition so that
    // validateEntity (and strict rollback) never flags them as undefined.
    await defineAttribute(
      runner,
      normalized.exists.toType,
      SKOLEM_ORIGIN_ATTR,
      'String',
      false,
      'Skolem origin rule (managed by applyExistentialRules)'
    );
  }

  await runDslRows(
    runner,
    query()
      .input({
        rule_name: param('rule_name', rn),
        spec_json: param('spec_json', JSON.stringify(_stableNormalizeForJson(normalized))),
        mode: param('mode', mode),
        message: param('message', message),
        enabled: param('enabled', enabled),
      })
      .put('om_existential_rule_def', ['rule_name'], ['spec_json', 'mode', 'message', 'enabled'])
  );

  return { ruleName: rn, spec: normalized, mode, message, enabled };
}

async function _resolveExistentialRuleRuntime(runner, rule) {
  // Re-resolve every name at run time so rules stay correct after schema
  // renames (the stored spec keeps the canonical names of define time).
  const bodyCanonical = await resolveType(runner, rule.spec.forEach.type);
  const bodyTypes = [bodyCanonical, ...(await getDescendants(runner, bodyCanonical))];
  const bodyTypeNames = new Set();
  for (const t of bodyTypes) {
    bodyTypeNames.add(t);
    for (const a of await _listTypeAliasesForCanonical(runner, t)) bodyTypeNames.add(a);
  }

  const relCanonical = await resolveRel(runner, rule.spec.exists.rel);
  const relNames = [relCanonical, ...(await _listRelAliasesForCanonical(runner, relCanonical))];

  const toCanonical = await resolveType(runner, rule.spec.exists.toType);
  const toTypes = [toCanonical, ...(await getDescendants(runner, toCanonical))];
  const toTypeNames = new Set();
  for (const t of toTypes) {
    toTypeNames.add(t);
    for (const a of await _listTypeAliasesForCanonical(runner, t)) toTypeNames.add(a);
  }

  return {
    bodyTypeNames: [...bodyTypeNames].sort((l, r) => l.localeCompare(r)),
    relNames,
    toTypeNames: [...toTypeNames],
    direction: _normalizeExistentialDirection(rule.spec.exists.direction),
    relCanonical,
    toCanonical,
  };
}

const _EXISTENTIAL_OP_TO_COZO = { '=': '==', '!=': '!=', '>': '>', '>=': '>=', '<': '<', '<=': '<=' };

async function _findExistentialViolations(runner, rule, asOf) {
  const rt = await _resolveExistentialRuleRuntime(runner, rule);
  const at = asOf ? '@ $as_of' : '@ "NOW"';

  const params = { rel_names: rt.relNames, to_types: rt.toTypeNames };
  if (asOf) params.as_of = asOf;

  const whereConds = (rule.spec.forEach && Array.isArray(rule.spec.forEach.where))
    ? rule.spec.forEach.where
    : [];
  let whereAtoms = '';
  whereConds.forEach((cond, i) => {
    const op = _EXISTENTIAL_OP_TO_COZO[cond.op];
    if (!op) throw new Error(`Unsupported where op '${cond.op}' in rule '${rule.ruleName}'`);
    params[`w_attr_${i}`] = cond.attr;
    params[`w_value_${i}`] = cond.value;
    whereAtoms += `,
  *om_property{ entity_id: id, attr_name: $w_attr_${i}, value: w_val_${i} ${at} }, w_val_${i} ${op} $w_value_${i}`;
  });

  const satEdgeAtom = rt.direction === 'in'
    ? `*om_edge{ from_id: other_id, rel_name: rn, to_id: id, props: _p ${at} }`
    : `*om_edge{ from_id: id, rel_name: rn, to_id: other_id, props: _p ${at} }`;

  const script = `
sat[id] := ${satEdgeAtom}, is_in(rn, $rel_names),
  *om_entity{ id: other_id, type_name: other_type, label: _other_label }, is_in(other_type, $to_types)
?[id] := *om_entity{ id, type_name: $type_name, label: _label }${whereAtoms},
  not sat[id]
:order id
  `.trim();

  const violations = [];
  for (const typeName of rt.bodyTypeNames) {
    const rows = await runRows(runner, script, { ...params, type_name: typeName });
    for (const [id] of rows) {
      violations.push({ rule: rule.ruleName, entityId: id, message: rule.message || '' });
    }
  }
  return violations;
}

async function checkExistentialRules(runner, options) {
  const opts = options && typeof options === 'object' ? options : {};
  const asOf = opts.asOf != null && opts.asOf !== ''
    ? _normalizeAsOfTimestamp(opts.asOf, 'asOf')
    : null;
  const wanted = Array.isArray(opts.rules) && opts.rules.length
    ? new Set(opts.rules.map((r) => String(r)))
    : null;

  const rules = await listExistentialRules(runner);
  const violations = [];
  for (const rule of rules) {
    if (!rule.enabled) continue;
    if (wanted && !wanted.has(rule.ruleName)) continue;
    if (!rule.spec || !rule.spec.forEach || !rule.spec.exists) continue;
    violations.push(...(await _findExistentialViolations(runner, rule, asOf)));
  }

  violations.sort((l, r) => {
    const byRule = l.rule.localeCompare(r.rule);
    if (byRule !== 0) return byRule;
    return String(l.entityId).localeCompare(String(r.entityId));
  });
  return violations;
}

function _skolemIdFor(ruleName, entityId) {
  const digest = createHash('sha256').update(`${ruleName}|${entityId}`).digest('hex').slice(0, 16);
  return `skolem:${digest}`;
}

function _skolemLabelFor(rule, triggerEntityId) {
  const template = rule.spec.materialize && rule.spec.materialize.labelTemplate
    ? String(rule.spec.materialize.labelTemplate)
    : '';
  if (template) {
    return template
      .split('{fromId}').join(triggerEntityId)
      .split('{rule}').join(rule.ruleName);
  }
  return `skolem:${rule.ruleName}:${triggerEntityId}`;
}

async function applyExistentialRules(runner, options) {
  const opts = options && typeof options === 'object' ? options : {};
  const maxIterations = opts.maxIterations != null ? Number(opts.maxIterations) : 10;
  if (!Number.isFinite(maxIterations) || maxIterations < 1) {
    throw new Error('maxIterations must be a positive number');
  }
  const validTime = opts.validTime != null && opts.validTime !== '' ? String(opts.validTime) : '';
  const wanted = Array.isArray(opts.rules) && opts.rules.length
    ? new Set(opts.rules.map((r) => String(r)))
    : null;

  const allRules = await listExistentialRules(runner);
  const rules = allRules.filter((r) => {
    if (!r.enabled || r.mode !== 'materialize') return false;
    if (wanted && !wanted.has(r.ruleName)) return false;
    return !!(r.spec && r.spec.forEach && r.spec.exists);
  });

  // Skip constraint hooks here: chase is a system-level repair operation and
  // the violation re-check below is its own gate.
  const writeOpts = validTime
    ? { validTime, skipConstraints: true }
    : { skipConstraints: true };

  const created = [];
  const attempted = new Set();
  let iterations = 0;

  for (let iter = 1; iter <= maxIterations; iter++) {
    iterations = iter;
    let roundCreated = 0;

    for (const rule of rules) {
      const rt = await _resolveExistentialRuleRuntime(runner, rule);
      const violations = await _findExistentialViolations(runner, rule, null);
      for (const v of violations) {
        // \u0001 keeps the (rule, entity) key unambiguous: plain concatenation
        // would let ('r1' + '2x') shadow ('r12' + 'x').
        const attemptKey = `${rule.ruleName}\u0001${v.entityId}`;
        // A violation that survived its own materialization (e.g. a future
        // validTime) would otherwise re-create the same Skolem id forever.
        if (attempted.has(attemptKey)) continue;
        attempted.add(attemptKey);

        const skolemId = _skolemIdFor(rule.ruleName, v.entityId);
        await upsertEntity(runner, skolemId, rt.toCanonical, _skolemLabelFor(rule, v.entityId));
        await setProperty(runner, skolemId, SKOLEM_ORIGIN_ATTR, rule.ruleName, writeOpts);

        const props = rule.spec.materialize && rule.spec.materialize.props
          ? rule.spec.materialize.props
          : {};
        for (const [attrName, value] of Object.entries(props)) {
          await setProperty(runner, skolemId, attrName, value, writeOpts);
        }

        if (rt.direction === 'in') {
          await linkEntities(runner, skolemId, rt.relCanonical, v.entityId, {}, writeOpts);
        } else {
          await linkEntities(runner, v.entityId, rt.relCanonical, skolemId, {}, writeOpts);
        }

        created.push({
          rule: rule.ruleName,
          triggerEntityId: v.entityId,
          skolemId,
          rel: rt.relCanonical,
          toType: rt.toCanonical,
        });
        roundCreated++;
      }
    }

    if (!roundCreated) break;
  }

  // Post-check: fixpoint means no materialize-rule violations remain.
  const remainingByRule = new Map();
  for (const rule of rules) {
    const remaining = await _findExistentialViolations(runner, rule, null);
    if (remaining.length) remainingByRule.set(rule.ruleName, remaining.length);
  }
  const reachedFixpoint = remainingByRule.size === 0;
  const diagnostics = [...remainingByRule.entries()]
    .sort((l, r) => l[0].localeCompare(r[0]))
    .map(([ruleName, remainingViolations]) => ({ ruleName, remainingViolations }));

  return { created, iterations, reachedFixpoint, diagnostics };
}

async function listExistentialRules(runner) {
  const rows = await runDslRows(
    runner,
    query()
      .select(['rule_name', 'spec_json', 'mode', 'message', 'enabled'])
      .fromStored('om_existential_rule_def', {
        rule_name: dsl.var('rule_name'),
        spec_json: dsl.var('spec_json'),
        mode: dsl.var('mode'),
        message: dsl.var('message'),
        enabled: dsl.var('enabled'),
      })
      .order('rule_name')
  ).catch((e) => {
    if (_isStoredRelationMissingError(e)) return [];
    throw e;
  });

  const out = [];
  for (const [ruleName, specJson, mode, message, enabled] of rows) {
    let parsedSpec = null;
    try {
      parsedSpec = JSON.parse(specJson);
    } catch (_) {
      parsedSpec = null;
    }
    out.push({
      ruleName,
      spec: parsedSpec,
      mode,
      message,
      enabled: !!enabled,
    });
  }
  return out;
}

module.exports = {
  initSchema,
  createSchema,
  seedPermissionMetadata,
  checkAccess,
  getSchemaState,
  listSchemaVersions,
  readSchemaSnapshot,
  writeSchemaSnapshot,
  diffSchemaVersions,
  applySchemaMigration,
  rollbackSchema,
  resolveType,
  resolveRel,
  resolveAttr,
  invalidateAliasCache,
  defineTypeAlias,
  defineRelationAlias,
  defineAttributeAlias,
  defineType,
  defineMixin,
  defineAttribute,
  defineRelation,
  defineAction,
  executeAction,
  callParentAction,
  defineMutation,
  executeMutations,
  addInterceptor,
  defineConstraint,
  validateConstraints,
  defineComputed,
  createEntity,
  upsertEntity,
  deleteEntity,
  inferValueType,
  getEntityType,
  getAncestors,
  getDescendants,
  isSubtypeOf,
  getTypeHierarchy,
  getAttributeDefinitions,
  validatePropertyType,
  setProperty,
  getProperty,
  getPropertyHistory,
  getPropertyAsOf,
  validateRelation,
  linkEntities,
  unlinkEntities,
  validateRequiredProperties,
  validateEntity,
  finalizeEntity,
  getEntityView,
  getEntityViewAsOf,
  getNeighbors,
  getNeighborsAsOf,
  getEdgeHistory,
  traverse,
  findByType,
  aggregateByType,
  impactAnalysis,
  ownershipTree,
  riskHotspot,
  ingestBatch,
  defineExistentialRule,
  listExistentialRules,
  checkExistentialRules,
  applyExistentialRules,
  clearRegistry,
};
