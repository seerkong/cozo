const { createHash } = require('crypto');

const ALLOWED_VALUE_TYPES = new Set(['String', 'Number', 'Bool', 'Json', 'Validity']);
const dsl = require('./cozo-dsl');
const { query, param } = dsl;

const _OM_RUNTIME_BRAND = Symbol('cozo.om.runtime');
const _OM_RESOLUTION_SCOPE_BRAND = Symbol('cozo.om.resolution-scope');
const _OM_REGISTRY_OWNER = Symbol('cozo.om.registry-owner');
const _OM_BOUND_REGISTRY_SNAPSHOT = Symbol('cozo.om.bound-registry-snapshot');
const _BEHAVIOR_SCOPE = Symbol('cozo.om.behavior-scope');

function _emptyRegistrySnapshot() {
  return Object.freeze({
    actions: new Map(),
    mutations: new Map(),
    interceptors: new Map(),
    constraints: new Map(),
    validators: new Map(),
    computed: new Map(),
  });
}

function _createBehaviorGate() {
  let tail = Promise.resolve();
  let pending = 0;
  return {
    isIdle() {
      return pending === 0;
    },
    async enter() {
      pending++;
      let releaseNext;
      const next = new Promise((resolve) => {
        releaseNext = resolve;
      });
      const previous = tail;
      tail = previous.then(() => next);
      await previous;
      let released = false;
      return () => {
        if (released) return;
        released = true;
        pending--;
        releaseNext();
      };
    },
  };
}

function _createRegistryOwner() {
  return {
    snapshot: _emptyRegistrySnapshot(),
    behaviorGate: _createBehaviorGate(),
  };
}

const _legacyRegistryOwner = _createRegistryOwner();

function _isOmRuntime(value) {
  return !!value && value[_OM_RUNTIME_BRAND] === true;
}

function _isResolutionScope(value) {
  return !!value && value[_OM_RESOLUTION_SCOPE_BRAND] === true;
}

function _runnerOf(value) {
  return _isOmRuntime(value) || _isResolutionScope(value) ? value.runner : value;
}

function ensureRunner(runner) {
  const rawRunner = _runnerOf(runner);
  if (!rawRunner || typeof rawRunner.run !== 'function') {
    throw new Error('Runner must provide a run(script, params?) method');
  }
  return rawRunner;
}

function createOmRuntime(runner) {
  const rawRunner = ensureRunner(runner);
  return Object.freeze({
    [_OM_RUNTIME_BRAND]: true,
    runner: rawRunner,
    [_OM_REGISTRY_OWNER]: _createRegistryOwner(),
  });
}

function _registryOwnerOf(value) {
  if (_isOmRuntime(value) || _isResolutionScope(value)) {
    return value[_OM_REGISTRY_OWNER];
  }
  return _legacyRegistryOwner;
}

function _captureResolutionScope(value) {
  if (_isResolutionScope(value)) return value;
  const registryOwner = _registryOwnerOf(value);
  const registrySnapshot = _isOmRuntime(value)
    && Object.prototype.hasOwnProperty.call(value, _OM_BOUND_REGISTRY_SNAPSHOT)
    ? value[_OM_BOUND_REGISTRY_SNAPSHOT]
    : registryOwner.snapshot;
  return Object.freeze({
    [_OM_RESOLUTION_SCOPE_BRAND]: true,
    runner: ensureRunner(value),
    [_OM_REGISTRY_OWNER]: registryOwner,
    registrySnapshot,
  });
}

function _scopeWithRunner(scope, runner) {
  const next = {
    [_OM_RESOLUTION_SCOPE_BRAND]: true,
    runner: ensureRunner(runner),
    [_OM_REGISTRY_OWNER]: scope[_OM_REGISTRY_OWNER],
    registrySnapshot: scope.registrySnapshot,
  };
  if (scope.behaviorBindingSnapshot instanceof Map) {
    next.behaviorBindingSnapshot = scope.behaviorBindingSnapshot;
  }
  return Object.freeze(next);
}

function _registrySnapshotOf(value) {
  if (_isResolutionScope(value)) return value.registrySnapshot;
  if (_isOmRuntime(value)
      && Object.prototype.hasOwnProperty.call(value, _OM_BOUND_REGISTRY_SNAPSHOT)) {
    return value[_OM_BOUND_REGISTRY_SNAPSHOT];
  }
  return _registryOwnerOf(value).snapshot;
}

async function _withBehaviorGate(value, callback) {
  const release = await _registryOwnerOf(value).behaviorGate.enter();
  try {
    return await callback();
  } finally {
    release();
  }
}

function _runtimeForScope(scope) {
  return Object.freeze({
    [_OM_RUNTIME_BRAND]: true,
    runner: scope.runner,
    [_OM_REGISTRY_OWNER]: scope[_OM_REGISTRY_OWNER],
    [_OM_BOUND_REGISTRY_SNAPSHOT]: scope.registrySnapshot,
  });
}

function _publishRegistry(owner, key, update) {
  const current = owner.snapshot;
  const nextRegistry = update(current[key]);
  owner.snapshot = Object.freeze({ ...current, [key]: nextRegistry });
}

function _setTypeBehavior(owner, registryKey, typeName, behaviorName, value) {
  _publishRegistry(owner, registryKey, (currentRegistry) => {
    const nextRegistry = new Map(currentRegistry);
    const nextTypeMap = new Map(currentRegistry.get(typeName) || []);
    nextTypeMap.set(behaviorName, value);
    nextRegistry.set(typeName, nextTypeMap);
    return nextRegistry;
  });
}

function _deleteTypeBehavior(owner, registryKey, typeName, behaviorName) {
  _publishRegistry(owner, registryKey, (currentRegistry) => {
    const currentTypeMap = currentRegistry.get(typeName);
    if (!currentTypeMap || !currentTypeMap.has(behaviorName)) return currentRegistry;
    const nextRegistry = new Map(currentRegistry);
    const nextTypeMap = new Map(currentTypeMap);
    nextTypeMap.delete(behaviorName);
    if (nextTypeMap.size) {
      nextRegistry.set(typeName, nextTypeMap);
    } else {
      nextRegistry.delete(typeName);
    }
    return nextRegistry;
  });
}

function clearRegistry(runtime) {
  if (runtime === undefined) {
    _legacyRegistryOwner.snapshot = _emptyRegistrySnapshot();
    return;
  }
  if (!_isOmRuntime(runtime)) {
    throw new Error('clearRegistry expects an OM runtime created by createOmRuntime');
  }
  const owner = runtime[_OM_REGISTRY_OWNER];
  if (owner.behaviorGate.isIdle()) {
    owner.snapshot = _emptyRegistrySnapshot();
    return;
  }
  return _withBehaviorGate(runtime, () => {
    owner.snapshot = _emptyRegistrySnapshot();
  });
}

async function runRows(runner, script, params) {
  const rawRunner = ensureRunner(runner);
  const result = await rawRunner.run(script, params || {});
  return result && Array.isArray(result.rows) ? result.rows : [];
}

async function runDslRows(runner, builder) {
  const { script, params } = builder.build();
  return runRows(runner, script, params);
}

const _BEHAVIOR_KINDS = ['constraint', 'computed', 'action', 'mutation', 'interceptor'];
const _BEHAVIOR_SLOTS = ['when', 'then', 'validator', 'compute', 'handler', 'executor'];
const _BEHAVIOR_READINESS = ['unbound', 'unresolved', 'ready'];
const _NON_INTERCEPTOR_PHASE = '';
const _NON_INTERCEPTOR_SEQ = -1;

class BehaviorUnresolvedError extends Error {
  constructor(diagnostic) {
    const safeDiagnostic = Object.freeze({ ...diagnostic });
    super(
      `Behavior callback is unresolved: ${safeDiagnostic.behaviorKey} `
      + `slot=${safeDiagnostic.slot} bindingId=${safeDiagnostic.bindingId}`
    );
    this.name = 'BehaviorUnresolvedError';
    this.diagnostic = safeDiagnostic;
    Object.assign(this, safeDiagnostic);
  }
}

class BehaviorImportError extends Error {
  constructor(message, originalFailure, compensationFailures = []) {
    super(message, { cause: originalFailure });
    this.name = 'BehaviorImportError';
    this.originalFailure = originalFailure;
    this.compensationFailures = Object.freeze([...compensationFailures]);
  }
}

class BehaviorRegistryPublicationConflictError extends Error {
  constructor() {
    super('Behavior registry changed after import staging; staged publication was rejected.');
    this.name = 'BehaviorRegistryPublicationConflictError';
  }
}

function _ordinalCompare(left, right) {
  const a = String(left);
  const b = String(right);
  return a < b ? -1 : a > b ? 1 : 0;
}

function _behaviorKindOrder(kind) {
  return _BEHAVIOR_KINDS.indexOf(kind);
}

function _behaviorSlotOrder(slot) {
  return _BEHAVIOR_SLOTS.indexOf(slot);
}

function _behaviorReadinessOrder(readiness) {
  return _BEHAVIOR_READINESS.indexOf(readiness);
}

function _compareBehaviorEntries(left, right) {
  return _behaviorKindOrder(left.kind) - _behaviorKindOrder(right.kind)
    || _ordinalCompare(left.ownerType, right.ownerType)
    || _ordinalCompare(left.name, right.name)
    || _ordinalCompare(left.interceptorPhase || '', right.interceptorPhase || '')
    || (left.interceptorSeq == null ? _NON_INTERCEPTOR_SEQ : left.interceptorSeq)
      - (right.interceptorSeq == null ? _NON_INTERCEPTOR_SEQ : right.interceptorSeq);
}

function _compareBehaviorCallbacks(left, right) {
  return _behaviorSlotOrder(left.slot) - _behaviorSlotOrder(right.slot)
    || _ordinalCompare(left.bindingId || '', right.bindingId || '')
    || _behaviorReadinessOrder(left.readiness) - _behaviorReadinessOrder(right.readiness);
}

function _freezeBehaviorCatalog(catalog) {
  const behaviors = catalog.behaviors.map((entry) => Object.freeze({
    ...entry,
    callbacks: Object.freeze(entry.callbacks.map((callback) => Object.freeze({ ...callback }))),
  }));
  return Object.freeze({ behaviors: Object.freeze(behaviors) });
}

function _normalizeBehaviorCatalog(catalog) {
  if (!catalog || !Array.isArray(catalog.behaviors)) {
    throw new TypeError('Behavior catalog must provide a behaviors array');
  }
  return _freezeBehaviorCatalog({
    behaviors: catalog.behaviors.map((entry) => ({
      kind: entry.kind,
      ownerType: entry.ownerType,
      name: entry.name,
      constraintType: entry.constraintType == null ? null : entry.constraintType,
      message: entry.message == null ? null : entry.message,
      description: entry.description == null ? null : entry.description,
      interceptorPhase: entry.interceptorPhase == null ? null : entry.interceptorPhase,
      interceptorSeq: entry.interceptorSeq == null ? null : entry.interceptorSeq,
      callbacks: (Array.isArray(entry.callbacks) ? entry.callbacks : []).map((callback) => ({
        slot: callback.slot,
        bindingId: callback.bindingId == null ? null : callback.bindingId,
        readiness: callback.readiness,
      })).sort(_compareBehaviorCallbacks),
    })).sort(_compareBehaviorEntries),
  });
}

function _bindingKey(kind, ownerType, behaviorName, slot, phase, seq) {
  return [kind, ownerType, behaviorName, slot, phase, seq].join('\u001f');
}

function _requireRegistrationRuntime(runtime, apiName) {
  if (!_isOmRuntime(runtime)) {
    throw new TypeError(`${apiName} expects an OM runtime created by createOmRuntime`);
  }
  return runtime[_OM_REGISTRY_OWNER];
}

function _requireBehaviorKey(value, fieldName) {
  const key = String(value || '').trim();
  if (!key) throw new TypeError(`${fieldName} is required`);
  return key;
}

function _requireCallback(value, fieldName) {
  if (typeof value !== 'function') {
    throw new TypeError(`${fieldName} must be a function`);
  }
  return value;
}

function _requireBindingId(value, fieldName = 'bindingId') {
  if (typeof value !== 'string' || !value.trim()) {
    throw new TypeError(`${fieldName} must be a non-empty string`);
  }
  return value;
}

function _registrationArgs(bindingOrCallback, maybeCallback, callbackName) {
  if (typeof bindingOrCallback === 'function' && maybeCallback === undefined) {
    return {
      bindingId: null,
      callback: _requireCallback(bindingOrCallback, callbackName),
    };
  }
  return {
    bindingId: _requireBindingId(bindingOrCallback),
    callback: _requireCallback(maybeCallback, callbackName),
  };
}

function registerConstraint(runtime, typeName, constraintName, ...args) {
  const owner = _requireRegistrationRuntime(runtime, 'registerConstraint');
  const tn = _requireBehaviorKey(typeName, 'Type name');
  const cn = _requireBehaviorKey(constraintName, 'Constraint name');
  let when;
  let then;
  let whenBindingId = null;
  let thenBindingId = null;

  if (args.length === 2) {
    [when, then] = args;
  } else if (args.length === 4) {
    [whenBindingId, when, thenBindingId, then] = args;
    whenBindingId = _requireBindingId(whenBindingId, 'whenBindingId');
    thenBindingId = _requireBindingId(thenBindingId, 'thenBindingId');
  } else {
    throw new TypeError(
      'registerConstraint expects (runtime, typeName, constraintName, when, then) '
      + 'or (runtime, typeName, constraintName, whenBindingId, when, thenBindingId, then)'
    );
  }

  _setTypeBehavior(owner, 'constraints', tn, cn, {
    when: _requireCallback(when, 'when'),
    then: _requireCallback(then, 'then'),
    whenBindingId,
    thenBindingId,
    ownerType: tn,
  });
}

function registerValidator(
  runtime,
  typeName,
  constraintName,
  bindingOrValidator,
  maybeValidator
) {
  const owner = _requireRegistrationRuntime(runtime, 'registerValidator');
  const tn = _requireBehaviorKey(typeName, 'Type name');
  const cn = _requireBehaviorKey(constraintName, 'Constraint name');
  const { bindingId, callback } = _registrationArgs(
    bindingOrValidator,
    maybeValidator,
    'validator'
  );
  _setTypeBehavior(owner, 'validators', tn, cn, {
    validator: callback,
    bindingId,
    ownerType: tn,
  });
}

function registerComputed(runtime, typeName, attrName, bindingOrCompute, maybeCompute) {
  const owner = _requireRegistrationRuntime(runtime, 'registerComputed');
  const tn = _requireBehaviorKey(typeName, 'Type name');
  const an = _requireBehaviorKey(attrName, 'Attribute name');
  const { bindingId, callback } = _registrationArgs(
    bindingOrCompute,
    maybeCompute,
    'compute'
  );
  _setTypeBehavior(owner, 'computed', tn, an, {
    computeFn: callback,
    bindingId,
    ownerType: tn,
  });
}

function registerAction(runtime, typeName, actionName, bindingOrHandler, maybeHandler) {
  const owner = _requireRegistrationRuntime(runtime, 'registerAction');
  const tn = _requireBehaviorKey(typeName, 'Type name');
  const an = _requireBehaviorKey(actionName, 'Action name');
  const { bindingId, callback } = _registrationArgs(
    bindingOrHandler,
    maybeHandler,
    'handler'
  );
  _setTypeBehavior(owner, 'actions', tn, an, {
    handler: callback,
    bindingId,
    ownerType: tn,
  });
}

function registerMutation(runtime, typeName, mutationName, bindingOrExecutor, maybeExecutor) {
  const owner = _requireRegistrationRuntime(runtime, 'registerMutation');
  const tn = _requireBehaviorKey(typeName, 'Type name');
  const mn = _requireBehaviorKey(mutationName, 'Mutation name');
  const { bindingId, callback } = _registrationArgs(
    bindingOrExecutor,
    maybeExecutor,
    'executor'
  );
  _setTypeBehavior(owner, 'mutations', tn, mn, {
    executor: callback,
    bindingId,
    ownerType: tn,
  });
}

function registerInterceptor(
  runtime,
  typeName,
  actionName,
  phase,
  seq,
  bindingOrHandler,
  handlerOrDescription,
  maybeDescription
) {
  const owner = _requireRegistrationRuntime(runtime, 'registerInterceptor');
  const tn = _requireBehaviorKey(typeName, 'Type name');
  const an = _requireBehaviorKey(actionName, 'Action name');
  const ph = _normalizeInterceptorPhase(phase);
  if (!Number.isInteger(seq) || seq < 0) {
    throw new TypeError('Interceptor seq must be a non-negative integer');
  }

  const unbound = typeof bindingOrHandler === 'function';
  const bindingId = unbound ? null : _requireBindingId(bindingOrHandler);
  const handler = _requireCallback(
    unbound ? bindingOrHandler : handlerOrDescription,
    'handler'
  );
  const description = String(
    unbound ? handlerOrDescription || '' : maybeDescription || ''
  );
  const currentTypeMap = owner.snapshot.interceptors.get(tn);
  const current = currentTypeMap && currentTypeMap.get(an);
  const next = {
    before: [...(current ? current.before : [])],
    after: [...(current ? current.after : [])],
  };
  const registration = {
    handler,
    seq,
    description,
    ownerType: tn,
    bindingId,
  };
  const index = next[ph].findIndex((candidate) => candidate.seq === seq);
  if (index >= 0) {
    next[ph][index] = registration;
  } else {
    next[ph].push(registration);
    next[ph].sort((left, right) => left.seq - right.seq);
  }
  _setTypeBehavior(owner, 'interceptors', tn, an, next);
}

function _runtimeBindingId(snapshot, kind, ownerType, behaviorName, slot, phase, seq) {
  let registration = null;
  if (kind === 'constraint') {
    if (slot === 'validator') {
      const owner = snapshot.validators && snapshot.validators.get(ownerType);
      registration = owner && owner.get(behaviorName);
    } else {
      const owner = snapshot.constraints.get(ownerType);
      registration = owner && owner.get(behaviorName);
      if (registration) {
        return slot === 'when'
          ? registration.whenBindingId || null
          : registration.thenBindingId || null;
      }
    }
  } else if (kind === 'computed') {
    const owner = snapshot.computed.get(ownerType);
    registration = owner && owner.get(behaviorName);
  } else if (kind === 'action') {
    const owner = snapshot.actions.get(ownerType);
    registration = owner && owner.get(behaviorName);
  } else if (kind === 'mutation') {
    const owner = snapshot.mutations.get(ownerType);
    registration = owner && owner.get(behaviorName);
  } else if (kind === 'interceptor') {
    const owner = snapshot.interceptors.get(ownerType);
    const behavior = owner && owner.get(behaviorName);
    registration = behavior
      && (behavior[phase] || []).find((candidate) => candidate.seq === seq);
  }
  return registration && typeof registration.bindingId === 'string'
    ? registration.bindingId
    : null;
}

async function _captureBehaviorResolutionScopeUnlocked(value) {
  if (_isResolutionScope(value) && value.behaviorBindingSnapshot instanceof Map) {
    return value;
  }
  const scope = _captureResolutionScope(value);
  const bindingRows = await runRows(
    scope,
    '?[kind, owner, name, slot, phase, seq, binding_id] := *om_behavior_binding{behavior_kind: kind, owner_type: owner, behavior_name: name, callback_slot: slot, phase, seq, binding_id}'
  );
  const bindings = new Map(bindingRows.map(
    ([kind, owner, name, slot, phase, seq, bindingId]) => [
      _bindingKey(kind, owner, name, slot, phase, seq),
      bindingId == null ? '' : String(bindingId),
    ]
  ));
  return Object.freeze({
    [_OM_RESOLUTION_SCOPE_BRAND]: true,
    runner: scope.runner,
    [_OM_REGISTRY_OWNER]: scope[_OM_REGISTRY_OWNER],
    registrySnapshot: scope.registrySnapshot,
    behaviorBindingSnapshot: bindings,
  });
}

async function _captureBehaviorResolutionScope(value) {
  if (_isResolutionScope(value) && value.behaviorBindingSnapshot instanceof Map) {
    return value;
  }
  const gate = _registryOwnerOf(value).behaviorGate;
  const invocationScope = gate.isIdle() ? _captureResolutionScope(value) : null;
  return _withBehaviorGate(value, () =>
    _captureBehaviorResolutionScopeUnlocked(invocationScope || value)
  );
}

function _displayBehaviorKey(kind, ownerType, behaviorName, phase, seq) {
  return kind === 'interceptor'
    ? `${kind}:${ownerType}/${behaviorName}/${phase}/${seq}`
    : `${kind}:${ownerType}/${behaviorName}`;
}

function _unresolvedDiagnostic(kind, ownerType, behaviorName, slot, bindingId, phase, seq) {
  const isInterceptor = kind === 'interceptor';
  return {
    code: 'OMR1001',
    kind,
    ownerType,
    behaviorKey: _displayBehaviorKey(kind, ownerType, behaviorName, phase, seq),
    slot,
    bindingId,
    interceptorPhase: isInterceptor ? phase : null,
    interceptorSeq: isInterceptor ? seq : null,
  };
}

function _ensureReadyIfBound(
  scope,
  kind,
  ownerType,
  behaviorName,
  slot,
  phase = _NON_INTERCEPTOR_PHASE,
  seq = _NON_INTERCEPTOR_SEQ
) {
  const bindings = scope.behaviorBindingSnapshot;
  if (!(bindings instanceof Map)) {
    throw new Error('Behavior binding snapshot was not captured');
  }
  const key = _bindingKey(kind, ownerType, behaviorName, slot, phase, seq);
  if (!bindings.has(key)) return;
  const bindingId = bindings.get(key);
  const runtimeBindingId = _runtimeBindingId(
    scope.registrySnapshot,
    kind,
    ownerType,
    behaviorName,
    slot,
    phase,
    seq
  );
  if (runtimeBindingId !== bindingId) {
    throw new BehaviorUnresolvedError(
      _unresolvedDiagnostic(kind, ownerType, behaviorName, slot, bindingId, phase, seq)
    );
  }
}

function _projectBehaviorCallback(snapshot, bindings, kind, ownerType, name, slot, phase, seq) {
  const key = _bindingKey(kind, ownerType, name, slot, phase, seq);
  const bindingId = bindings.has(key) ? bindings.get(key) : null;
  if (bindingId === null) {
    return { slot, bindingId: null, readiness: 'unbound' };
  }
  const runtimeBindingId = _runtimeBindingId(
    snapshot,
    kind,
    ownerType,
    name,
    slot,
    phase,
    seq
  );
  return {
    slot,
    bindingId,
    readiness: runtimeBindingId === bindingId ? 'ready' : 'unresolved',
  };
}

async function _getBehaviorCatalogUnlocked(runner, registrySnapshot = null) {
  const scope = _captureResolutionScope(runner);
  const constraintRows = await runRows(
    scope,
    '?[owner, name, constraint_type, message] := *om_constraint_def{type_name: owner, constraint_name: name, constraint_type, message}'
  );
  const computedRows = await runRows(
    scope,
    '?[owner, name, description] := *om_computed_def{type_name: owner, attr_name: name, description}'
  );
  const actionRows = await runRows(
    scope,
    '?[owner, name, description] := *om_action_def{type_name: owner, action_name: name, description}'
  );
  const mutationRows = await runRows(
    scope,
    '?[owner, name, description] := *om_mutation_def{type_name: owner, mutation_name: name, description}'
  );
  const interceptorRows = await runRows(
    scope,
    '?[owner, name, phase, seq, description] := *om_interceptor_def{type_name: owner, action_name: name, phase, seq, description}'
  );
  const bindingRows = await runRows(
    scope,
    '?[kind, owner, name, slot, phase, seq, binding_id] := *om_behavior_binding{behavior_kind: kind, owner_type: owner, behavior_name: name, callback_slot: slot, phase, seq, binding_id}'
  );

  const bindings = new Map(bindingRows.map(
    ([kind, owner, name, slot, phase, seq, bindingId]) => [
      _bindingKey(kind, owner, name, slot, phase, seq),
      bindingId,
    ]
  ));
  const snapshot = registrySnapshot || scope.registrySnapshot;
  const behaviors = [];

  for (const [owner, name, constraintType, message] of constraintRows) {
    const slots = new Set(
      String(constraintType).toLowerCase() === 'custom'
        ? ['validator']
        : ['when', 'then']
    );
    for (const [kind, bindingOwner, bindingName, slot] of bindingRows) {
      if (kind === 'constraint' && bindingOwner === owner && bindingName === name) {
        slots.add(slot);
      }
    }
    behaviors.push({
      kind: 'constraint',
      ownerType: owner == null ? '' : String(owner),
      name: name == null ? '' : String(name),
      constraintType: constraintType == null ? '' : String(constraintType),
      message: message == null ? '' : String(message),
      description: null,
      interceptorPhase: null,
      interceptorSeq: null,
      callbacks: [...slots].sort(
        (left, right) => _behaviorSlotOrder(left) - _behaviorSlotOrder(right)
      ).map((slot) => _projectBehaviorCallback(
        snapshot,
        bindings,
        'constraint',
        owner,
        name,
        slot,
        _NON_INTERCEPTOR_PHASE,
        _NON_INTERCEPTOR_SEQ
      )),
    });
  }

  const addSingleSlotDefinitions = (rows, kind, slot) => {
    for (const [owner, name, description] of rows) {
      behaviors.push({
        kind,
        ownerType: owner == null ? '' : String(owner),
        name: name == null ? '' : String(name),
        constraintType: null,
        message: null,
        description: description == null ? '' : String(description),
        interceptorPhase: null,
        interceptorSeq: null,
        callbacks: [_projectBehaviorCallback(
          snapshot,
          bindings,
          kind,
          owner,
          name,
          slot,
          _NON_INTERCEPTOR_PHASE,
          _NON_INTERCEPTOR_SEQ
        )],
      });
    }
  };
  addSingleSlotDefinitions(computedRows, 'computed', 'compute');
  addSingleSlotDefinitions(actionRows, 'action', 'handler');
  addSingleSlotDefinitions(mutationRows, 'mutation', 'executor');

  for (const [owner, name, phase, seq, description] of interceptorRows) {
    behaviors.push({
      kind: 'interceptor',
      ownerType: owner == null ? '' : String(owner),
      name: name == null ? '' : String(name),
      constraintType: null,
      message: null,
      description: description == null ? '' : String(description),
      interceptorPhase: phase == null ? '' : String(phase),
      interceptorSeq: Number(seq),
      callbacks: [_projectBehaviorCallback(
        snapshot,
        bindings,
        'interceptor',
        owner,
        name,
        'handler',
        phase,
        Number(seq)
      )],
    });
  }

  return _normalizeBehaviorCatalog({ behaviors });
}

async function getBehaviorCatalog(runner) {
  return _withBehaviorGate(runner, async () => {
    const snapshot = _registrySnapshotOf(runner);
    return _getBehaviorCatalogUnlocked(runner, snapshot);
  });
}

function _jsonHex(codeUnit) {
  return codeUnit.toString(16).toUpperCase().padStart(4, '0');
}

function _systemTextJsonString(value) {
  const input = String(value);
  let output = '"';
  for (let index = 0; index < input.length; index++) {
    const codeUnit = input.charCodeAt(index);
    if (codeUnit >= 0xD800 && codeUnit <= 0xDBFF) {
      const low = index + 1 < input.length ? input.charCodeAt(index + 1) : -1;
      if (low >= 0xDC00 && low <= 0xDFFF) {
        output += `\\u${_jsonHex(codeUnit)}\\u${_jsonHex(low)}`;
        index++;
      } else {
        output += '\\uFFFD';
      }
      continue;
    }
    if (codeUnit >= 0xDC00 && codeUnit <= 0xDFFF) {
      output += '\\uFFFD';
      continue;
    }
    switch (codeUnit) {
      case 0x08:
        output += '\\b';
        break;
      case 0x09:
        output += '\\t';
        break;
      case 0x0A:
        output += '\\n';
        break;
      case 0x0C:
        output += '\\f';
        break;
      case 0x0D:
        output += '\\r';
        break;
      case 0x5C:
        output += '\\\\';
        break;
      default: {
        const mustEscape = codeUnit < 0x20
          || codeUnit > 0x7E
          || codeUnit === 0x22
          || codeUnit === 0x26
          || codeUnit === 0x27
          || codeUnit === 0x2B
          || codeUnit === 0x3C
          || codeUnit === 0x3E
          || codeUnit === 0x60;
        output += mustEscape ? `\\u${_jsonHex(codeUnit)}` : input[index];
        break;
      }
    }
  }
  return `${output}"`;
}

function _jsonNullableString(value) {
  return value == null ? 'null' : _systemTextJsonString(value);
}

function encodeBehaviorManifestJson(catalog) {
  const normalized = _normalizeBehaviorCatalog(catalog);
  const behaviors = normalized.behaviors.map((behavior) => {
    const callbacks = behavior.callbacks.map((callback) =>
      `{"slot":${_systemTextJsonString(callback.slot)},`
      + `"bindingId":${_jsonNullableString(callback.bindingId)},`
      + `"readiness":${_systemTextJsonString(callback.readiness)}}`
    ).join(',');
    return `{"kind":${_systemTextJsonString(behavior.kind)},`
      + `"ownerType":${_systemTextJsonString(behavior.ownerType)},`
      + `"name":${_systemTextJsonString(behavior.name)},`
      + `"constraintType":${_jsonNullableString(behavior.constraintType)},`
      + `"message":${_jsonNullableString(behavior.message)},`
      + `"description":${_jsonNullableString(behavior.description)},`
      + `"interceptorPhase":${_jsonNullableString(behavior.interceptorPhase)},`
      + `"interceptorSeq":${behavior.interceptorSeq == null ? 'null' : behavior.interceptorSeq},`
      + `"callbacks":[${callbacks}]}`;
  }).join(',');
  return new TextEncoder().encode(`{"version":1,"behaviors":[${behaviors}]}`);
}

function _manifestDiagnostic(code, path, message) {
  return Object.freeze({ code, path, message });
}

function _sortManifestDiagnostics(diagnostics) {
  return diagnostics.sort((left, right) =>
    _ordinalCompare(left.code, right.code)
    || _ordinalCompare(left.path, right.path)
    || _ordinalCompare(left.message, right.message)
  );
}

function _manifestFailure(diagnostics) {
  const ordered = Object.freeze(_sortManifestDiagnostics(diagnostics).map(
    (diagnostic) => Object.freeze({ ...diagnostic })
  ));
  return Object.freeze({ catalog: null, diagnostics: ordered, success: false });
}

function _readRequiredManifestString(element, propertyName, path, diagnostics) {
  if (Object.prototype.hasOwnProperty.call(element, propertyName)
      && typeof element[propertyName] === 'string') {
    return element[propertyName];
  }
  diagnostics.push(_manifestDiagnostic(
    'OMM1001',
    `${path}.${propertyName}`,
    `Property '${propertyName}' must be a string.`
  ));
  return null;
}

function _readOptionalManifestString(element, propertyName, path, diagnostics) {
  if (!Object.prototype.hasOwnProperty.call(element, propertyName)
      || element[propertyName] === null) {
    return null;
  }
  if (typeof element[propertyName] === 'string') {
    return element[propertyName];
  }
  diagnostics.push(_manifestDiagnostic(
    'OMM1001',
    `${path}.${propertyName}`,
    `Property '${propertyName}' must be a string or null.`
  ));
  return null;
}

function _readOptionalManifestInt(element, propertyName, path, diagnostics) {
  if (!Object.prototype.hasOwnProperty.call(element, propertyName)
      || element[propertyName] === null) {
    return null;
  }
  const value = element[propertyName];
  if (Number.isInteger(value) && value >= -2147483648 && value <= 2147483647) {
    return value;
  }
  diagnostics.push(_manifestDiagnostic(
    'OMM1001',
    `${path}.${propertyName}`,
    `Property '${propertyName}' must be an integer or null.`
  ));
  return null;
}

function _parseBehaviorKind(value, path, diagnostics) {
  if (_BEHAVIOR_KINDS.includes(value)) return value;
  if (value !== null) {
    diagnostics.push(_manifestDiagnostic(
      'OMM1101',
      path,
      `Behavior kind '${value}' is unknown.`
    ));
  }
  return null;
}

function _parseBehaviorSlot(value, path, diagnostics) {
  if (_BEHAVIOR_SLOTS.includes(value)) return value;
  if (value !== null) {
    diagnostics.push(_manifestDiagnostic(
      'OMM1102',
      path,
      `Callback slot '${value}' is unknown.`
    ));
  }
  return null;
}

function _parseBehaviorReadiness(value, path, diagnostics) {
  if (_BEHAVIOR_READINESS.includes(value)) return value;
  if (value !== null) {
    diagnostics.push(_manifestDiagnostic(
      'OMM1103',
      path,
      `Callback readiness '${value}' is unknown.`
    ));
  }
  return null;
}

function _requireManifestString(kind, propertyName, value, path, diagnostics) {
  if (value === null) {
    diagnostics.push(_manifestDiagnostic(
      'OMM1203',
      `${path}.${propertyName}`,
      `Property '${propertyName}' must be a string for behavior kind '${kind}'.`
    ));
  }
}

function _requireManifestNull(kind, propertyName, value, path, diagnostics) {
  if (value !== null) {
    diagnostics.push(_manifestDiagnostic(
      'OMM1203',
      `${path}.${propertyName}`,
      `Property '${propertyName}' must be null for behavior kind '${kind}'.`
    ));
  }
}

function _validateBehaviorMetadata(
  kind,
  constraintType,
  message,
  description,
  phase,
  seq,
  path,
  diagnostics
) {
  if (kind === 'constraint') {
    _requireManifestString(kind, 'constraintType', constraintType, path, diagnostics);
    _requireManifestNull(kind, 'description', description, path, diagnostics);
    _requireManifestNull(kind, 'interceptorPhase', phase, path, diagnostics);
    _requireManifestNull(kind, 'interceptorSeq', seq, path, diagnostics);
  } else if (kind === 'computed' || kind === 'action' || kind === 'mutation') {
    _requireManifestNull(kind, 'constraintType', constraintType, path, diagnostics);
    _requireManifestNull(kind, 'message', message, path, diagnostics);
    _requireManifestNull(kind, 'interceptorPhase', phase, path, diagnostics);
    _requireManifestNull(kind, 'interceptorSeq', seq, path, diagnostics);
  } else if (kind === 'interceptor') {
    _requireManifestNull(kind, 'constraintType', constraintType, path, diagnostics);
    _requireManifestNull(kind, 'message', message, path, diagnostics);
    if ((phase !== 'before' && phase !== 'after') || seq === null || seq < 0) {
      diagnostics.push(_manifestDiagnostic(
        'OMM1201',
        path,
        'Interceptor keys require phase before/after and a non-negative sequence.'
      ));
    }
  }
}

function _validateBehaviorSlot(kind, slot, path, diagnostics) {
  const valid = kind === 'constraint'
    ? slot === 'when' || slot === 'then' || slot === 'validator'
    : kind === 'computed'
      ? slot === 'compute'
      : kind === 'action' || kind === 'interceptor'
        ? slot === 'handler'
        : kind === 'mutation' && slot === 'executor';
  if (!valid) {
    diagnostics.push(_manifestDiagnostic(
      'OMM1202',
      path,
      `Callback slot '${slot}' is invalid for behavior kind '${kind}'.`
    ));
  }
}

function _displayBehaviorKey(kind, ownerType, name, phase, seq) {
  return kind === 'interceptor'
    ? `${kind}:${ownerType || ''}/${name || ''}/${phase || ''}/${seq == null ? '' : seq}`
    : `${kind}:${ownerType || ''}/${name || ''}`;
}

function _readBehaviorCallback(element, path, diagnostics) {
  if (element === null || Array.isArray(element) || typeof element !== 'object') {
    diagnostics.push(_manifestDiagnostic('OMM1001', path, 'Callback entry must be an object.'));
    return null;
  }
  const slotValue = _readRequiredManifestString(element, 'slot', path, diagnostics);
  const slot = _parseBehaviorSlot(slotValue, `${path}.slot`, diagnostics);
  const bindingId = _readOptionalManifestString(element, 'bindingId', path, diagnostics);
  const readinessValue = _readRequiredManifestString(element, 'readiness', path, diagnostics);
  const readiness = _parseBehaviorReadiness(
    readinessValue,
    `${path}.readiness`,
    diagnostics
  );
  if (slot === null || readiness === null) return null;

  const bindingValid = readiness === 'unbound'
    ? bindingId === null
    : typeof bindingId === 'string' && bindingId.trim().length > 0;
  if (!bindingValid) {
    diagnostics.push(_manifestDiagnostic(
      'OMM1202',
      path,
      'Unbound callbacks require a null bindingId; unresolved and ready callbacks require a non-empty bindingId.'
    ));
  }
  return { slot, bindingId, readiness };
}

function _readBehaviorEntry(element, path, diagnostics, bindingKeys) {
  if (element === null || Array.isArray(element) || typeof element !== 'object') {
    diagnostics.push(_manifestDiagnostic('OMM1001', path, 'Behavior entry must be an object.'));
    return null;
  }

  const kindValue = _readRequiredManifestString(element, 'kind', path, diagnostics);
  const kind = _parseBehaviorKind(kindValue, `${path}.kind`, diagnostics);
  const ownerType = _readRequiredManifestString(element, 'ownerType', path, diagnostics);
  const name = _readRequiredManifestString(element, 'name', path, diagnostics);
  if (ownerType === null || name === null || ownerType.trim() === '' || name.trim() === '') {
    diagnostics.push(_manifestDiagnostic(
      'OMM1201',
      path,
      'Behavior ownerType and name must be non-empty strings.'
    ));
  }

  const constraintType = _readOptionalManifestString(
    element,
    'constraintType',
    path,
    diagnostics
  );
  const message = _readOptionalManifestString(element, 'message', path, diagnostics);
  const description = _readOptionalManifestString(element, 'description', path, diagnostics);
  const phase = _readOptionalManifestString(element, 'interceptorPhase', path, diagnostics);
  const seq = _readOptionalManifestInt(element, 'interceptorSeq', path, diagnostics);
  if (kind !== null) {
    _validateBehaviorMetadata(
      kind,
      constraintType,
      message,
      description,
      phase,
      seq,
      path,
      diagnostics
    );
  }

  const callbacks = [];
  if (!Array.isArray(element.callbacks)) {
    diagnostics.push(_manifestDiagnostic(
      'OMM1001',
      `${path}.callbacks`,
      'Behavior callbacks must be an array.'
    ));
  } else {
    element.callbacks.forEach((callbackElement, index) => {
      const callbackPath = `${path}.callbacks[${index}]`;
      const callback = _readBehaviorCallback(callbackElement, callbackPath, diagnostics);
      if (callback !== null && kind !== null) {
        _validateBehaviorSlot(kind, callback.slot, callbackPath, diagnostics);
        const key = [
          kind,
          ownerType || '',
          name || '',
          phase || '',
          seq == null ? '' : seq,
          callback.slot,
        ].join('\u001f');
        if (bindingKeys.has(key)) {
          diagnostics.push(_manifestDiagnostic(
            'OMM1301',
            callbackPath,
            `Callback binding key '${_displayBehaviorKey(
              kind,
              ownerType,
              name,
              phase,
              seq
            )}/${callback.slot}' is duplicated or conflicting.`
          ));
        }
        bindingKeys.add(key);
        callbacks.push(callback);
      }
    });
  }

  if (kind === null) return null;
  return {
    kind,
    ownerType: ownerType || '',
    name: name || '',
    constraintType,
    message,
    description,
    interceptorPhase: phase,
    interceptorSeq: seq,
    callbacks,
  };
}

function decodeBehaviorManifestJson(json) {
  if (json === null || json === undefined) {
    return _manifestFailure([
      _manifestDiagnostic('OMM1000', '$', 'Manifest JSON is required.'),
    ]);
  }

  let source;
  try {
    source = typeof json === 'string' ? json : new TextDecoder().decode(json);
  } catch (_) {
    return _manifestFailure([
      _manifestDiagnostic('OMM1000', '$', 'Manifest JSON is malformed at line 0, byte 0.'),
    ]);
  }

  let root;
  try {
    root = JSON.parse(source);
  } catch (_) {
    return _manifestFailure([
      _manifestDiagnostic('OMM1000', '$', 'Manifest JSON is malformed at line 0, byte 0.'),
    ]);
  }

  const diagnostics = [];
  if (root === null || Array.isArray(root) || typeof root !== 'object') {
    return _manifestFailure([
      _manifestDiagnostic('OMM1001', '$', 'Manifest root must be an object.'),
    ]);
  }

  if (!Number.isInteger(root.version)
      || root.version < -2147483648
      || root.version > 2147483647) {
    diagnostics.push(_manifestDiagnostic(
      'OMM1001',
      '$.version',
      'Manifest version must be an integer.'
    ));
  } else if (root.version !== 1) {
    diagnostics.push(_manifestDiagnostic(
      'OMM1002',
      '$.version',
      `Manifest version '${root.version}' is not supported.`
    ));
  }

  if (!Array.isArray(root.behaviors)) {
    diagnostics.push(_manifestDiagnostic(
      'OMM1001',
      '$.behaviors',
      'Manifest behaviors must be an array.'
    ));
    return _manifestFailure(diagnostics);
  }

  const entries = [];
  const behaviorKeys = new Set();
  const bindingKeys = new Set();
  root.behaviors.forEach((behaviorElement, index) => {
    const path = `$.behaviors[${index}]`;
    const entry = _readBehaviorEntry(behaviorElement, path, diagnostics, bindingKeys);
    if (entry !== null) {
      const key = [
        entry.kind,
        entry.ownerType,
        entry.name,
        entry.interceptorPhase || '',
        entry.interceptorSeq == null ? '' : entry.interceptorSeq,
      ].join('\u001f');
      if (behaviorKeys.has(key)) {
        diagnostics.push(_manifestDiagnostic(
          'OMM1302',
          path,
          `Behavior key '${_displayBehaviorKey(
            entry.kind,
            entry.ownerType,
            entry.name,
            entry.interceptorPhase,
            entry.interceptorSeq
          )}' is duplicated or conflicting.`
        ));
      }
      behaviorKeys.add(key);
      entries.push(entry);
    }
  });

  if (diagnostics.length > 0) {
    return _manifestFailure(diagnostics);
  }
  return Object.freeze({
    catalog: _normalizeBehaviorCatalog({ behaviors: entries }),
    diagnostics: Object.freeze([]),
    success: true,
  });
}

async function exportBehaviorManifestJson(runner) {
  return encodeBehaviorManifestJson(await getBehaviorCatalog(runner));
}

function _importDiagnostic(code, entry, callback, bindingId, message, path = null) {
  return Object.freeze({
    code,
    path: path || (entry
      ? _displayBehaviorKey(
        entry.kind,
        entry.ownerType,
        entry.name,
        entry.interceptorPhase,
        entry.interceptorSeq
      )
      : '$callbacks'),
    message,
    kind: entry ? entry.kind : null,
    ownerType: entry ? entry.ownerType : null,
    behaviorName: entry ? entry.name : null,
    slot: callback ? callback.slot : null,
    bindingId: bindingId == null ? (callback ? callback.bindingId : null) : bindingId,
    interceptorPhase: entry ? entry.interceptorPhase : null,
    interceptorSeq: entry ? entry.interceptorSeq : null,
  });
}

function _sortImportDiagnostics(diagnostics) {
  return diagnostics.sort((left, right) =>
    _ordinalCompare(left.code, right.code)
    || _ordinalCompare(left.path, right.path)
    || _ordinalCompare(left.message, right.message)
  );
}

function _freezeImportResult(applied, diagnostics = [], unresolved = []) {
  return Object.freeze({
    applied,
    diagnostics: Object.freeze(
      _sortImportDiagnostics([...diagnostics]).map((item) => Object.freeze({ ...item }))
    ),
    unresolved: Object.freeze(
      [...unresolved].sort((left, right) =>
        _behaviorKindOrder(left.kind) - _behaviorKindOrder(right.kind)
        || _ordinalCompare(left.ownerType, right.ownerType)
        || _ordinalCompare(left.behaviorKey, right.behaviorKey)
        || _behaviorSlotOrder(left.slot) - _behaviorSlotOrder(right.slot)
      ).map((item) => Object.freeze({ ...item }))
    ),
  });
}

const _CALLBACK_SET_KINDS = Object.freeze([
  ['constraints', 'constraint'],
  ['validators', 'validator'],
  ['computed', 'computed'],
  ['actions', 'action'],
  ['mutations', 'mutation'],
  ['interceptors', 'interceptor'],
]);

function _createBehaviorCallbackIndex(callbacks) {
  const source = callbacks == null ? {} : callbacks;
  const diagnostics = [];
  const indexes = {};
  const allBindingIds = new Set();

  if (typeof source !== 'object' || Array.isArray(source)) {
    diagnostics.push(_importDiagnostic(
      'OMI1001',
      null,
      null,
      null,
      'Typed callback binding set must be an object.'
    ));
    return { indexes, allBindingIds, diagnostics };
  }

  for (const [property, label] of _CALLBACK_SET_KINDS) {
    const values = source[property] == null ? [] : source[property];
    const index = new Map();
    indexes[property] = index;
    if (!Array.isArray(values)) {
      diagnostics.push(_importDiagnostic(
        'OMI1001',
        null,
        null,
        null,
        `Typed callback collection '${property}' must be an array.`
      ));
      continue;
    }
    for (const item of values) {
      const bindingId = item && typeof item.bindingId === 'string'
        ? item.bindingId
        : null;
      const callback = item && item.callback;
      if (!bindingId || !bindingId.trim() || typeof callback !== 'function') {
        diagnostics.push(_importDiagnostic(
          'OMI1001',
          null,
          null,
          bindingId,
          'Typed callback binding requires a non-empty id and delegate.'
        ));
        continue;
      }
      allBindingIds.add(bindingId);
      if (index.has(bindingId)) {
        diagnostics.push(_importDiagnostic(
          'OMI1002',
          null,
          null,
          bindingId,
          `Typed callback binding '${bindingId}' is duplicated for the same delegate type.`
        ));
        continue;
      }
      index.set(bindingId, Object.freeze({ bindingId, callback, type: label }));
    }
  }
  return { indexes, allBindingIds, diagnostics };
}

function _expectedConstraintSlots(constraintType) {
  return constraintType === 'custom' ? ['validator'] : ['when', 'then'];
}

async function _validateImportCatalog(runtime, catalog) {
  const diagnostics = [];
  for (const entry of catalog.behaviors) {
    let canonicalOwner = null;
    try {
      canonicalOwner = await resolveType(runtime, entry.ownerType);
    } catch (_) {
      canonicalOwner = null;
    }
    if (canonicalOwner !== entry.ownerType
        || !(canonicalOwner && await _typeExists(runtime, canonicalOwner))) {
      diagnostics.push(_importDiagnostic(
        'OMI1101',
        entry,
        null,
        null,
        `Owner type '${entry.ownerType}' does not exist as a canonical type.`
      ));
    }

    if (entry.kind === 'constraint') {
      const constraintType = String(entry.constraintType || '').trim().toLowerCase();
      if (!['conditional', 'cross-entity', 'computed-dep', 'custom'].includes(
        constraintType
      )) {
        diagnostics.push(_importDiagnostic(
          'OMI1102',
          entry,
          null,
          null,
          `Constraint type '${entry.constraintType}' is unsupported.`
        ));
      } else {
        const expected = _expectedConstraintSlots(constraintType);
        const actual = new Set(entry.callbacks.map((callback) => callback.slot));
        const missing = expected.filter((slot) => !actual.has(slot));
        const extra = ['when', 'then', 'validator'].filter(
          (slot) => actual.has(slot) && !expected.includes(slot)
        );
        if (missing.length || extra.length) {
          const format = (slots) => slots.length ? slots.join(', ') : 'none';
          diagnostics.push(_importDiagnostic(
            'OMI1104',
            entry,
            null,
            null,
            `Constraint type '${constraintType}' requires exactly callback slots `
            + `[${format(expected)}]; missing slots [${format(missing)}]; `
            + `extra slots [${format(extra)}].`,
            `${_displayBehaviorKey(
              entry.kind,
              entry.ownerType,
              entry.name,
              entry.interceptorPhase,
              entry.interceptorSeq
            )}.callbacks`
          ));
        }
      }
    }

    if (entry.callbacks.length === 0) {
      diagnostics.push(_importDiagnostic(
        'OMI1103',
        entry,
        null,
        null,
        'Behavior entry must declare at least one callback slot.'
      ));
    }
  }
  return diagnostics;
}

function _snapshotSetTypeBehavior(snapshot, registryKey, ownerType, behaviorName, value) {
  const currentRegistry = snapshot[registryKey];
  const nextRegistry = new Map(currentRegistry);
  const nextTypeMap = new Map(currentRegistry.get(ownerType) || []);
  nextTypeMap.set(behaviorName, value);
  nextRegistry.set(ownerType, nextTypeMap);
  return Object.freeze({ ...snapshot, [registryKey]: nextRegistry });
}

function _snapshotDeleteTypeBehavior(snapshot, registryKey, ownerType, behaviorName) {
  const currentRegistry = snapshot[registryKey];
  const currentTypeMap = currentRegistry.get(ownerType);
  if (!currentTypeMap || !currentTypeMap.has(behaviorName)) return snapshot;
  const nextRegistry = new Map(currentRegistry);
  const nextTypeMap = new Map(currentTypeMap);
  nextTypeMap.delete(behaviorName);
  if (nextTypeMap.size) nextRegistry.set(ownerType, nextTypeMap);
  else nextRegistry.delete(ownerType);
  return Object.freeze({ ...snapshot, [registryKey]: nextRegistry });
}

function _snapshotDeleteInterceptor(snapshot, entry) {
  const ownerMap = snapshot.interceptors.get(entry.ownerType);
  const current = ownerMap && ownerMap.get(entry.name);
  if (!current) return snapshot;
  const phase = entry.interceptorPhase;
  const next = {
    before: [...current.before],
    after: [...current.after],
  };
  next[phase] = next[phase].filter((candidate) => candidate.seq !== entry.interceptorSeq);
  if (!next.before.length && !next.after.length) {
    return _snapshotDeleteTypeBehavior(
      snapshot,
      'interceptors',
      entry.ownerType,
      entry.name
    );
  }
  return _snapshotSetTypeBehavior(
    snapshot,
    'interceptors',
    entry.ownerType,
    entry.name,
    next
  );
}

function _snapshotSetInterceptor(snapshot, entry, callback, bindingId) {
  const ownerMap = snapshot.interceptors.get(entry.ownerType);
  const current = ownerMap && ownerMap.get(entry.name);
  const next = {
    before: [...(current ? current.before : [])],
    after: [...(current ? current.after : [])],
  };
  next[entry.interceptorPhase].push({
    handler: callback,
    seq: entry.interceptorSeq,
    description: entry.description || '',
    ownerType: entry.ownerType,
    bindingId,
  });
  next[entry.interceptorPhase].sort((left, right) => left.seq - right.seq);
  return _snapshotSetTypeBehavior(
    snapshot,
    'interceptors',
    entry.ownerType,
    entry.name,
    next
  );
}

function _resolveImportedCallback(
  callbackIndexes,
  collection,
  entry,
  callback,
  unresolved,
  diagnostics
) {
  if (callback.bindingId === null) {
    diagnostics.push(_importDiagnostic(
      'OMI2001',
      entry,
      callback,
      null,
      'Unbound callback cannot be made ready by manifest data.'
    ));
    return null;
  }

  const binding = callbackIndexes.indexes[collection].get(callback.bindingId);
  if (binding) return binding.callback;
  unresolved.push(_unresolvedDiagnostic(
    entry.kind,
    entry.ownerType,
    entry.name,
    callback.slot,
    callback.bindingId,
    entry.interceptorPhase || _NON_INTERCEPTOR_PHASE,
    entry.interceptorSeq == null ? _NON_INTERCEPTOR_SEQ : entry.interceptorSeq
  ));
  const incompatible = callbackIndexes.allBindingIds.has(callback.bindingId);
  diagnostics.push(_importDiagnostic(
    incompatible ? 'OMI1202' : 'OMI1201',
    entry,
    callback,
    callback.bindingId,
    incompatible
      ? `Binding '${callback.bindingId}' has an incompatible delegate type.`
      : `Binding '${callback.bindingId}' has no supplied typed callback.`
  ));
  return null;
}

function _missingImportedConstraint() {
  throw new Error('Unresolved imported constraint callback was invoked.');
}

function _stageImportedRegistry(source, catalog, callbackIndexes) {
  let snapshot = source;
  const unresolved = [];
  const diagnostics = [];

  for (const entry of catalog.behaviors) {
    if (entry.kind === 'constraint') {
      snapshot = _snapshotDeleteTypeBehavior(
        snapshot,
        'constraints',
        entry.ownerType,
        entry.name
      );
      snapshot = _snapshotDeleteTypeBehavior(
        snapshot,
        'validators',
        entry.ownerType,
        entry.name
      );
      if (String(entry.constraintType).trim().toLowerCase() === 'custom') {
        const callback = entry.callbacks[0];
        const validator = _resolveImportedCallback(
          callbackIndexes,
          'validators',
          entry,
          callback,
          unresolved,
          diagnostics
        );
        if (validator) {
          snapshot = _snapshotSetTypeBehavior(
            snapshot,
            'validators',
            entry.ownerType,
            entry.name,
            {
              validator,
              bindingId: callback.bindingId,
              ownerType: entry.ownerType,
            }
          );
        }
      } else {
        const bySlot = new Map(entry.callbacks.map((callback) => [callback.slot, callback]));
        const whenCallback = bySlot.get('when');
        const thenCallback = bySlot.get('then');
        const when = _resolveImportedCallback(
          callbackIndexes,
          'constraints',
          entry,
          whenCallback,
          unresolved,
          diagnostics
        );
        const then = _resolveImportedCallback(
          callbackIndexes,
          'constraints',
          entry,
          thenCallback,
          unresolved,
          diagnostics
        );
        snapshot = _snapshotSetTypeBehavior(
          snapshot,
          'constraints',
          entry.ownerType,
          entry.name,
          {
            constraintType: entry.constraintType,
            message: entry.message || '',
            when: when || _missingImportedConstraint,
            then: then || _missingImportedConstraint,
            whenBindingId: when ? whenCallback.bindingId : null,
            thenBindingId: then ? thenCallback.bindingId : null,
            ownerType: entry.ownerType,
          }
        );
      }
      continue;
    }

    const callback = entry.callbacks[0];
    const collection = entry.kind === 'computed'
      ? 'computed'
      : entry.kind === 'action'
        ? 'actions'
        : entry.kind === 'mutation'
          ? 'mutations'
          : 'interceptors';
    const registryKey = entry.kind === 'computed'
      ? 'computed'
      : entry.kind === 'action'
        ? 'actions'
        : entry.kind === 'mutation'
          ? 'mutations'
          : 'interceptors';
    snapshot = entry.kind === 'interceptor'
      ? _snapshotDeleteInterceptor(snapshot, entry)
      : _snapshotDeleteTypeBehavior(snapshot, registryKey, entry.ownerType, entry.name);
    const imported = _resolveImportedCallback(
      callbackIndexes,
      collection,
      entry,
      callback,
      unresolved,
      diagnostics
    );
    if (!imported) continue;

    if (entry.kind === 'computed') {
      snapshot = _snapshotSetTypeBehavior(snapshot, registryKey, entry.ownerType, entry.name, {
        computeFn: imported,
        description: entry.description || '',
        bindingId: callback.bindingId,
        ownerType: entry.ownerType,
      });
    } else if (entry.kind === 'action') {
      snapshot = _snapshotSetTypeBehavior(snapshot, registryKey, entry.ownerType, entry.name, {
        handler: imported,
        description: entry.description || '',
        bindingId: callback.bindingId,
        ownerType: entry.ownerType,
      });
    } else if (entry.kind === 'mutation') {
      snapshot = _snapshotSetTypeBehavior(snapshot, registryKey, entry.ownerType, entry.name, {
        executor: imported,
        description: entry.description || '',
        bindingId: callback.bindingId,
        ownerType: entry.ownerType,
      });
    } else {
      snapshot = _snapshotSetInterceptor(
        snapshot,
        entry,
        imported,
        callback.bindingId
      );
    }
  }

  return { snapshot, unresolved, diagnostics };
}

function _sameBehaviorKey(left, right) {
  return left.kind === right.kind
    && left.ownerType === right.ownerType
    && left.name === right.name
    && left.interceptorPhase === right.interceptorPhase
    && left.interceptorSeq === right.interceptorSeq;
}

async function _listBehaviorBindingRows(runner) {
  return runRows(
    runner,
    '?[kind, owner, name, slot, phase, seq, binding_id] := *om_behavior_binding{behavior_kind: kind, owner_type: owner, behavior_name: name, callback_slot: slot, phase, seq, binding_id}'
  );
}

function _bindingRowMatchesEntry(row, entry) {
  const [kind, owner, name, , phase, seq] = row;
  return kind === entry.kind
    && owner === entry.ownerType
    && name === entry.name
    && (entry.kind !== 'interceptor'
      || phase === entry.interceptorPhase && Number(seq) === entry.interceptorSeq);
}

async function _putBehaviorMetadata(runner, entry) {
  const common = {
    type_name: entry.ownerType,
    description: entry.description || '',
  };
  if (entry.kind === 'constraint') {
    return runRows(
      runner,
      '?[type_name, constraint_name, constraint_type, message] <- [[$type_name, $constraint_name, $constraint_type, $message]]\n:put om_constraint_def {type_name, constraint_name => constraint_type, message}',
      {
        type_name: entry.ownerType,
        constraint_name: entry.name,
        constraint_type: entry.constraintType || '',
        message: entry.message || '',
      }
    );
  }
  if (entry.kind === 'computed') {
    return runRows(
      runner,
      '?[type_name, attr_name, description] <- [[$type_name, $attr_name, $description]]\n:put om_computed_def {type_name, attr_name => description}',
      { ...common, attr_name: entry.name }
    );
  }
  if (entry.kind === 'action') {
    return runRows(
      runner,
      '?[type_name, action_name, description] <- [[$type_name, $action_name, $description]]\n:put om_action_def {type_name, action_name => description}',
      { ...common, action_name: entry.name }
    );
  }
  if (entry.kind === 'mutation') {
    return runRows(
      runner,
      '?[type_name, mutation_name, description] <- [[$type_name, $mutation_name, $description]]\n:put om_mutation_def {type_name, mutation_name => description}',
      { ...common, mutation_name: entry.name }
    );
  }
  return runRows(
    runner,
    '?[type_name, action_name, phase, seq, description] <- [[$type_name, $action_name, $phase, $seq, $description]]\n:put om_interceptor_def {type_name, action_name, phase, seq => description}',
    {
      ...common,
      action_name: entry.name,
      phase: entry.interceptorPhase,
      seq: entry.interceptorSeq,
    }
  );
}

async function _removeBehaviorMetadata(runner, entry) {
  const params = { type_name: entry.ownerType };
  if (entry.kind === 'constraint') {
    return runRows(
      runner,
      '?[type_name, constraint_name] <- [[$type_name, $constraint_name]]\n:rm om_constraint_def {type_name, constraint_name}',
      { ...params, constraint_name: entry.name }
    );
  }
  if (entry.kind === 'computed') {
    return runRows(
      runner,
      '?[type_name, attr_name] <- [[$type_name, $attr_name]]\n:rm om_computed_def {type_name, attr_name}',
      { ...params, attr_name: entry.name }
    );
  }
  if (entry.kind === 'action') {
    return runRows(
      runner,
      '?[type_name, action_name] <- [[$type_name, $action_name]]\n:rm om_action_def {type_name, action_name}',
      { ...params, action_name: entry.name }
    );
  }
  if (entry.kind === 'mutation') {
    return runRows(
      runner,
      '?[type_name, mutation_name] <- [[$type_name, $mutation_name]]\n:rm om_mutation_def {type_name, mutation_name}',
      { ...params, mutation_name: entry.name }
    );
  }
  return runRows(
    runner,
    '?[type_name, action_name, phase, seq] <- [[$type_name, $action_name, $phase, $seq]]\n:rm om_interceptor_def {type_name, action_name, phase, seq}',
    {
      ...params,
      action_name: entry.name,
      phase: entry.interceptorPhase,
      seq: entry.interceptorSeq,
    }
  );
}

async function _removeBehaviorBindings(runner, entry) {
  const params = {
    kind: entry.kind,
    owner: entry.ownerType,
    name: entry.name,
  };
  const interceptorFilter = entry.kind === 'interceptor'
    ? ', phase = $phase, seq = $seq'
    : '';
  return runRows(
    runner,
    `?[behavior_kind, owner_type, behavior_name, callback_slot, phase, seq] := `
      + '*om_behavior_binding{behavior_kind, owner_type, behavior_name, '
      + 'callback_slot, phase, seq}, '
      + 'behavior_kind = $kind, owner_type = $owner, behavior_name = $name'
      + `${interceptorFilter}\n`
      + ':rm om_behavior_binding {behavior_kind, owner_type, behavior_name, '
      + 'callback_slot, phase, seq}',
    {
      ...params,
      phase: entry.interceptorPhase,
      seq: entry.interceptorSeq,
    }
  );
}

async function _putBehaviorBindingRow(runner, row) {
  const [kind, owner, name, slot, phase, seq, bindingId] = row;
  return runRows(
    runner,
    '?[behavior_kind, owner_type, behavior_name, callback_slot, phase, seq, binding_id] '
      + '<- [[$kind, $owner, $name, $slot, $phase, $seq, $binding_id]]\n'
      + ':put om_behavior_binding {behavior_kind, owner_type, behavior_name, '
      + 'callback_slot, phase, seq => binding_id}',
    { kind, owner, name, slot, phase, seq, binding_id: bindingId }
  );
}

async function _applyImportedPersistentState(runner, entries) {
  for (const entry of entries) {
    await _putBehaviorMetadata(runner, entry);
    await _removeBehaviorBindings(runner, entry);
    for (const callback of entry.callbacks) {
      if (callback.bindingId === null) continue;
      await _putBehaviorBindingRow(runner, [
        entry.kind,
        entry.ownerType,
        entry.name,
        callback.slot,
        entry.interceptorPhase || _NON_INTERCEPTOR_PHASE,
        entry.interceptorSeq == null ? _NON_INTERCEPTOR_SEQ : entry.interceptorSeq,
        callback.bindingId,
      ]);
    }
  }
}

async function _restoreImportedPersistentState(
  runner,
  importedEntries,
  previousEntries,
  previousBindings
) {
  for (const entry of importedEntries) {
    await _removeBehaviorMetadata(runner, entry);
    await _removeBehaviorBindings(runner, entry);
  }
  await _applyImportedPersistentState(runner, previousEntries);
  for (const row of previousBindings) {
    await _putBehaviorBindingRow(runner, row);
  }
}

async function _runBehaviorWriteTransaction(runtime, callback) {
  const rawRunner = ensureRunner(runtime);
  if (typeof rawRunner.multiTransact !== 'function') {
    throw new Error('Behavior import requires a CozoDb instance with multiTransact(write)');
  }
  const tx = rawRunner.multiTransact(true);
  const scope = _scopeWithRunner(_captureResolutionScope(runtime), tx);
  try {
    const result = await callback(scope);
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

async function importBehaviorManifestJson(runtime, json, callbacks, options) {
  if (!_isOmRuntime(runtime)) {
    throw new TypeError(
      'importBehaviorManifestJson expects an OM runtime created by createOmRuntime'
    );
  }
  const decoded = decodeBehaviorManifestJson(json);
  if (!decoded.success) {
    return _freezeImportResult(false, decoded.diagnostics.map((diagnostic) =>
      _importDiagnostic(
        diagnostic.code,
        null,
        null,
        null,
        diagnostic.message,
        diagnostic.path
      )
    ));
  }

  const callbackIndexes = _createBehaviorCallbackIndex(callbacks);
  if (callbackIndexes.diagnostics.length) {
    return _freezeImportResult(false, callbackIndexes.diagnostics);
  }

  return _withBehaviorGate(runtime, async () => {
    const catalog = decoded.catalog;
    const validationDiagnostics = await _validateImportCatalog(runtime, catalog);
    if (validationDiagnostics.length) {
      return _freezeImportResult(false, validationDiagnostics);
    }

    const owner = _registryOwnerOf(runtime);
    const preRegistry = owner.snapshot;
    const staged = _stageImportedRegistry(preRegistry, catalog, callbackIndexes);
    if (options && options.requireReady
        && (staged.unresolved.length || staged.diagnostics.length)) {
      return _freezeImportResult(false, staged.diagnostics, staged.unresolved);
    }

    const currentCatalog = await _getBehaviorCatalogUnlocked(runtime, preRegistry);
    const currentBindings = await _listBehaviorBindingRows(runtime);
    const affectedEntries = currentCatalog.behaviors.filter((current) =>
      catalog.behaviors.some((imported) => _sameBehaviorKey(current, imported))
    );
    const affectedBindings = currentBindings.filter((row) =>
      catalog.behaviors.some((entry) => _bindingRowMatchesEntry(row, entry))
    );

    try {
      await _runBehaviorWriteTransaction(
        runtime,
        (txRuntime) => _applyImportedPersistentState(txRuntime, catalog.behaviors)
      );
    } catch (failure) {
      throw new BehaviorImportError(
        'Behavior manifest persistence failed before registry publication.',
        failure
      );
    }

    try {
      if (owner.snapshot !== preRegistry) {
        throw new BehaviorRegistryPublicationConflictError();
      }
      owner.snapshot = staged.snapshot;
      return _freezeImportResult(true, staged.diagnostics, staged.unresolved);
    } catch (publishFailure) {
      const compensationFailures = [];
      try {
        await _runBehaviorWriteTransaction(
          runtime,
          (txRuntime) => _restoreImportedPersistentState(
            txRuntime,
            catalog.behaviors,
            affectedEntries,
            affectedBindings
          )
        );
      } catch (compensationFailure) {
        compensationFailures.push(compensationFailure);
      }
      throw new BehaviorImportError(
        compensationFailures.length
          ? 'Behavior registry publication failed and persistent compensation was incomplete.'
          : 'Behavior registry publication failed; persistent state was restored.',
        publishFailure,
        compensationFailures
      );
    }
  });
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

async function _actionDefinitionExists(runner, ownerType, actionName) {
  const rows = await runRows(
    runner,
    `
?[present] :=
  *om_action_def{type_name: $owner_type, action_name: $action_name, description: _description},
  present = true
:limit 1
    `.trim(),
    { owner_type: ownerType, action_name: actionName }
  );
  return rows.length > 0;
}

async function _mutationDefinitionExists(runner, ownerType, mutationName) {
  const rows = await runRows(
    runner,
    `
?[present] :=
  *om_mutation_def{type_name: $owner_type, mutation_name: $mutation_name, description: _description},
  present = true
:limit 1
    `.trim(),
    { owner_type: ownerType, mutation_name: mutationName }
  );
  return rows.length > 0;
}

async function _listInterceptorSequences(runner, ownerType, actionName, phase) {
  const rows = await runRows(
    runner,
    `
?[seq] :=
  *om_interceptor_def{
    type_name: $owner_type,
    action_name: $action_name,
    phase: $phase,
    seq,
    description: _description
  }
:sort seq
    `.trim(),
    { owner_type: ownerType, action_name: actionName, phase }
  );
  return rows
    .map(([seq]) => Number(seq))
    .filter((seq) => Number.isInteger(seq) && seq >= 0);
}

async function _listComputedDefinitions(runner, ownerType) {
  const rows = await runRows(
    runner,
    `
?[attr_name] :=
  *om_computed_def{type_name: $owner_type, attr_name, description: _description}
:sort attr_name
    `.trim(),
    { owner_type: ownerType }
  );
  return rows
    .map(([attrName]) => String(attrName || '').trim())
    .filter(Boolean);
}

function _normalizeConstraintType(scope) {
  const s = String(scope || '').trim().toLowerCase();
  if (!s || s === 'conditional') return 'conditional';
  if (s === 'cross-entity' || s === 'cross_entity') return 'cross-entity';
  if (s === 'computed-dep' || s === 'computed_dep') return 'computed-dep';
  if (s === 'custom') return 'custom';
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
  const validator = def.validator;

  if (constraintType === 'custom') {
    if (typeof validator !== 'function') {
      throw new Error('Custom constraint definition must provide a validator(ctx) function');
    }
    if (whenFn !== undefined || thenFn !== undefined) {
      throw new Error('Custom constraint definition cannot provide when(ctx) or then(ctx)');
    }
  } else {
    if (validator !== undefined) {
      throw new Error('Scoped constraint definition cannot provide validator(ctx)');
    }
    if (typeof whenFn !== 'function') {
      throw new Error('Constraint definition must provide a when(ctx) function');
    }
    if (typeof thenFn !== 'function') {
      throw new Error('Constraint definition must provide a then(ctx) function');
    }
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

  const owner = _registryOwnerOf(runner);
  if (constraintType === 'custom') {
    _deleteTypeBehavior(owner, 'constraints', tn, cn);
    _setTypeBehavior(owner, 'validators', tn, cn, {
      validator,
      bindingId: null,
      ownerType: tn,
    });
  } else {
    _deleteTypeBehavior(owner, 'validators', tn, cn);
    _setTypeBehavior(owner, 'constraints', tn, cn, {
      constraintType,
      message,
      when: whenFn,
      then: thenFn,
      whenBindingId: null,
      thenBindingId: null,
      ownerType: tn,
    });
  }
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

  _setTypeBehavior(_registryOwnerOf(runner), 'computed', tn, an, {
    computeFn,
    description: String(description || ''),
    bindingId: null,
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
      const map = _registrySnapshotOf(runner).computed.get(t);
      return map && map.get(an);
    }
  );
}

async function _getComputedMapForType(runner, typeName) {
  const tn = String(typeName || '').trim();
  if (!tn) throw new Error('Type name is required');
  const ancestors = await _getAncestorList(runner, tn);
  const chain = [...ancestors].reverse();
  const registry = _registrySnapshotOf(runner).computed;

  const merged = new Map();
  for (const t of chain) {
    const map = registry.get(t);
    if (!map) continue;
    for (const [attrName, def] of map.entries()) {
      merged.set(attrName, def);
    }
  }
  const self = registry.get(tn);
  if (self) {
    for (const [attrName, def] of self.entries()) {
      merged.set(attrName, def);
    }
  }
  return merged;
}

async function _listEffectiveComputedDefinitions(runner, typeName) {
  const scope = await _captureBehaviorResolutionScope(runner);
  const tn = String(typeName || '').trim();
  if (!tn) throw new Error('Type name is required');
  const ancestors = await _getAncestorList(scope, tn);
  const chain = [...ancestors].reverse();
  const merged = new Map();
  for (const ownerType of chain) {
    for (const attrName of await _listComputedDefinitions(scope, ownerType)) {
      merged.set(attrName, { ownerType, attrName });
    }
  }
  for (const attrName of await _listComputedDefinitions(scope, tn)) {
    merged.set(attrName, { ownerType: tn, attrName });
  }
  return [...merged.values()].sort(
    (left, right) => _ordinalCompare(left.attrName, right.attrName)
  );
}

async function _resolveComputedCallback(runner, typeName, attrName) {
  const scope = await _captureBehaviorResolutionScope(runner);
  const tn = String(typeName || '').trim();
  const an = String(attrName || '').trim();
  if (!tn) throw new Error('Type name is required');
  if (!an) throw new Error('Attribute name is required');
  const canonicalAttrName = await _resolveAttrForCanonicalType(scope, tn, an);
  for (const definition of await _listEffectiveComputedDefinitions(scope, tn)) {
    const definitionAttrName = await _resolveAttrForCanonicalType(
      scope,
      tn,
      definition.attrName
    );
    if (definitionAttrName !== canonicalAttrName) continue;
    _ensureReadyIfBound(
      scope,
      'computed',
      definition.ownerType,
      definition.attrName,
      'compute'
    );
    const typeMap = scope.registrySnapshot.computed.get(definition.ownerType);
    const registration = typeMap && typeMap.get(definition.attrName);
    return registration && typeof registration.computeFn === 'function'
      ? registration
      : null;
  }
  return null;
}

async function validateConstraints(runner, entityId, options) {
  const scope = await _captureBehaviorResolutionScope(runner);
  const id = String(entityId || '').trim();
  if (!id) throw new Error('entityId is required');

  const wantedTypes = options && Array.isArray(options.types) ? options.types : null;
  const wantedSet = wantedTypes ? new Set(wantedTypes.map((t) => String(t))) : null;

  const typeName = await getEntityType(scope, id);
  const ancestors = await _getAncestorList(scope, typeName);
  const chain = [typeName, ...ancestors].reverse();
  const constraintRegistry = scope.registrySnapshot.constraints;
  const validatorRegistry = scope.registrySnapshot.validators;
  const definitionRows = await runRows(
    scope,
    '?[owner, name, constraint_type, message] := *om_constraint_def{type_name: owner, constraint_name: name, constraint_type, message}'
  );
  const definitionsByOwner = new Map();
  for (const [owner, name, constraintType, message] of definitionRows) {
    if (!definitionsByOwner.has(owner)) definitionsByOwner.set(owner, []);
    definitionsByOwner.get(owner).push({
      name,
      constraintType: String(constraintType),
      message: message == null ? '' : String(message),
    });
  }
  for (const definitions of definitionsByOwner.values()) {
    definitions.sort((left, right) => _ordinalCompare(left.name, right.name));
  }

  const ctx = {
    runner: scope.runner,
    runtime: _runtimeForScope(scope),
    entityId: id,
    typeName,
    getProperty: async (attrName) => getProperty(scope, id, attrName),
    getNeighbors: async (relName, direction) => getNeighbors(scope, id, relName, direction),
  };

  const errors = [];
  const resolvedConstraints = [];

  for (const t of chain) {
    const definitions = definitionsByOwner.get(t) || [];
    for (const definition of definitions) {
      if (wantedSet && !wantedSet.has(definition.constraintType)) continue;

      if (definition.constraintType === 'custom') {
        _ensureReadyIfBound(
          scope,
          'constraint',
          t,
          definition.name,
          'validator'
        );
        _ensureReadyIfBound(scope, 'constraint', t, definition.name, 'when');
        _ensureReadyIfBound(scope, 'constraint', t, definition.name, 'then');
        const validator = validatorRegistry.get(t)
          && validatorRegistry.get(t).get(definition.name);
        resolvedConstraints.push({ definition, validator, constraint: null });
        continue;
      }

      _ensureReadyIfBound(scope, 'constraint', t, definition.name, 'when');
      _ensureReadyIfBound(scope, 'constraint', t, definition.name, 'then');
      const def = constraintRegistry.get(t)
        && constraintRegistry.get(t).get(definition.name);
      resolvedConstraints.push({ definition, validator: null, constraint: def || null });
    }
  }

  for (const resolved of resolvedConstraints) {
    const { definition, validator, constraint } = resolved;
    if (validator) {
      try {
        const message = await validator.validator(ctx);
        if (message !== null) {
          if (typeof message !== 'string') {
            throw new TypeError('Custom validator must return string or null');
          }
          errors.push(message);
        }
      } catch (e) {
        if (e instanceof BehaviorUnresolvedError) throw e;
        errors.push(
          `Constraint '${definition.name}' evaluation failed (validator): ${e.message || e}`
        );
      }
      continue;
    }
    if (!constraint) continue;
    let active = false;
    try {
      active = await constraint.when(ctx);
    } catch (e) {
      if (e instanceof BehaviorUnresolvedError) throw e;
      errors.push(
        `Constraint '${definition.name}' evaluation failed (when): ${e.message || e}`
      );
      continue;
    }
    if (!active) continue;
    let ok = false;
    try {
      ok = await constraint.then(ctx);
    } catch (e) {
      if (e instanceof BehaviorUnresolvedError) throw e;
      errors.push(
        `Constraint '${definition.name}' evaluation failed (then): ${e.message || e}`
      );
      continue;
    }
    if (!ok) {
      const msg = definition.message ? `: ${definition.message}` : '';
      errors.push(`Constraint '${definition.name}' violated${msg}`);
    }
  }

  return { valid: errors.length === 0, errors };
}

async function _withWriteTxIfPossible(runner, fn) {
  const scope = _captureResolutionScope(runner);
  const rawRunner = scope.runner;
  if (typeof rawRunner.multiTransact === 'function') {
    const tx = rawRunner.multiTransact(true);
    try {
      const result = await fn(_scopeWithRunner(scope, tx));
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
  return fn(scope);
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

  _setTypeBehavior(
    _registryOwnerOf(runner),
    'mutations',
    tn,
    mn,
    {
      executor,
      description: String(description || ''),
      bindingId: null,
      ownerType: tn,
    }
  );
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

  _setTypeBehavior(
    _registryOwnerOf(runner),
    'actions',
    tn,
    an,
    {
      handler,
      description: String(description || ''),
      bindingId: null,
      ownerType: tn,
    }
  );
}

async function _resolveActionDef(runner, typeName, actionName) {
  const scope = await _captureBehaviorResolutionScope(runner);
  const tn = String(typeName || '').trim();
  const an = String(actionName || '').trim();
  if (!tn) throw new Error('Type name is required');
  if (!an) throw new Error('Action name is required');

  const chain = [tn, ...(await _getAncestorList(scope, tn))];
  for (const ownerType of chain) {
    if (await _actionDefinitionExists(scope, ownerType, an)) {
      _ensureReadyIfBound(scope, 'action', ownerType, an, 'handler');
    }
    const map = scope.registrySnapshot.actions.get(ownerType);
    const hit = map && map.get(an);
    if (hit) return hit;
  }
  return null;
}

async function callParentAction(ctx, actionName, params) {
  if (!ctx || typeof ctx !== 'object') {
    throw new Error('Action context is required');
  }
  const scope = ctx[_BEHAVIOR_SCOPE] || await _captureBehaviorResolutionScope(ctx.runner);
  const runner = scope.runner;
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

  const parentType = await _getParentType(scope, currentOwnerType);
  if (!parentType) {
    throw new Error(`Action '${an}' has no parent action (type '${currentOwnerType}' has no parentType)`);
  }

  const chain = [parentType, ...(await _getAncestorList(scope, parentType))];
  let parentDef = null;
  for (const t of chain) {
    if (await _actionDefinitionExists(scope, t, an)) {
      _ensureReadyIfBound(scope, 'action', t, an, 'handler');
    }
    const map = scope.registrySnapshot.actions.get(t);
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
    runtime: ctx.runtime || _runtimeForScope(scope),
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
  Object.defineProperty(nextCtx, _BEHAVIOR_SCOPE, { value: scope });
  nextCtx.callParentAction = (name, p) => callParentAction(nextCtx, name, p);

  const mutations = await parentDef.handler(nextCtx, nextCtx.params);
  const list = mutations == null ? [] : mutations;
  if (!Array.isArray(list)) {
    throw new Error(`Action '${an}' must return an array of mutations`);
  }
  return list;
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

  const owner = _registryOwnerOf(runner);
  const currentRegistry = owner.snapshot.interceptors;
  const currentTypeMap = currentRegistry.get(tn);
  const currentEntry = currentTypeMap && currentTypeMap.get(an);
  const entry = currentEntry || { before: [], after: [] };
  const persistedRows = await runRows(
    runner,
    `
?[seq] :=
  *om_interceptor_def{
    type_name: $type_name,
    action_name: $action_name,
    phase: $phase,
    seq
  }
    `.trim(),
    { type_name: tn, action_name: an, phase: ph }
  );
  const persistedMax = persistedRows.reduce(
    (max, [seq]) => Number.isInteger(seq) ? Math.max(max, seq) : max,
    -1
  );
  const runtimeMax = entry[ph].reduce(
    (max, candidate) => Math.max(max, candidate.seq),
    -1
  );
  const seq = Math.max(persistedMax, runtimeMax) + 1;

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

  const nextEntry = {
    before: [...entry.before],
    after: [...entry.after],
  };
  nextEntry[ph].push({
    handler,
    seq,
    description: String(description || ''),
    ownerType: tn,
    bindingId: null,
  });
  _setTypeBehavior(owner, 'interceptors', tn, an, nextEntry);
}

async function _collectInterceptors(runner, entityTypeName, actionName) {
  const scope = await _captureBehaviorResolutionScope(runner);
  const tn = String(entityTypeName || '').trim();
  const an = String(actionName || '').trim();
  if (!tn) throw new Error('Type name is required');
  if (!an) throw new Error('Action name is required');

  // Apply ancestor interceptors first.
  const chain = [tn, ...(await _getAncestorList(scope, tn))].reverse();
  const registry = scope.registrySnapshot.interceptors;

  const collectPhase = async (phase) => {
    const resolved = [];
    for (const ownerType of chain) {
      const typeMap = registry.get(ownerType);
      const entry = typeMap && typeMap.get(an);
      const registrations = entry ? [...(entry[phase] || [])].sort((left, right) => left.seq - right.seq) : [];
      const metadataSequences = await _listInterceptorSequences(scope, ownerType, an, phase);
      const metadataSet = new Set(metadataSequences);
      for (const seq of metadataSequences) {
        _ensureReadyIfBound(
          scope,
          'interceptor',
          ownerType,
          an,
          'handler',
          phase,
          seq
        );
        const registration = registrations.find((candidate) => candidate.seq === seq);
        if (registration) resolved.push(registration);
      }
      for (const registration of registrations) {
        if (!metadataSet.has(registration.seq)) resolved.push(registration);
      }
    }
    return resolved;
  };

  return {
    before: await collectPhase('before'),
    after: await collectPhase('after'),
  };
}

async function executeAction(db, entityId, actionName, params) {
  const scope = await _captureBehaviorResolutionScope(db);
  const rawDb = scope.runner;
  if (typeof rawDb.multiTransact !== 'function') {
    throw new Error('executeAction requires a CozoDb instance with multiTransact(write)');
  }

  const id = String(entityId || '').trim();
  const an = String(actionName || '').trim();
  if (!id) throw new Error('entityId is required');
  if (!an) throw new Error('Action name is required');

  const tx = rawDb.multiTransact(true);
  const txScope = _scopeWithRunner(scope, tx);
  try {
    const typeName = await getEntityType(txScope, id);
    const def = await _resolveActionDef(txScope, typeName, an);
    if (!def) {
      throw new Error(`Action '${an}' not defined for type '${typeName}'`);
    }
    const interceptors = await _collectInterceptors(txScope, typeName, an);

    const ctx = {
      runner: tx,
      runtime: _runtimeForScope(txScope),
      entityId: id,
      typeName,
      actionOwnerType: def.ownerType || typeName,
      params: params || {},
      // These helpers are intended for future phases; for now they support basic handlers.
      getProperty: async (attrName, options) => {
        const opts = options && typeof options === 'object' ? options : {};
        const asOf = Object.prototype.hasOwnProperty.call(opts, 'asOf') ? String(opts.asOf || '').trim() : '';
        if (asOf) {
          return getPropertyAsOf(txScope, id, attrName, asOf);
        }
        return getProperty(txScope, id, attrName);
      },
      setProperty: async (attrName, value, options) => setProperty(txScope, id, attrName, value, options),
      linkEntities: async (relName, toId, props, options) =>
        linkEntities(txScope, id, relName, toId, props, options),
      getNeighbors: async (relName, direction) => getNeighbors(txScope, id, relName, direction),
    };

    Object.defineProperty(ctx, _BEHAVIOR_SCOPE, { value: txScope });
    ctx.callParentAction = (name, p) => callParentAction(ctx, name, p);

    for (const it of interceptors.before) {
      await it.handler(ctx);
    }

    const mutations = await def.handler(ctx, ctx.params);
    const list = mutations == null ? [] : mutations;
    if (!Array.isArray(list)) {
      throw new Error(`Action '${an}' must return an array of mutations`);
    }

    await _executeMutationsInRunner(txScope, id, list);

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
  const scope = await _captureBehaviorResolutionScope(runner);
  const list = Array.isArray(mutations) ? mutations : [];
  const id = String(entityId || '').trim();
  if (!id) throw new Error('entityId is required');

  const typeName = await getEntityType(scope, id);

  const ctx = {
    runner: scope.runner,
    runtime: _runtimeForScope(scope),
    entityId: id,
    typeName,
    getProperty: async (attrName, options) => {
      const opts = options && typeof options === 'object' ? options : {};
      const asOf = Object.prototype.hasOwnProperty.call(opts, 'asOf') ? String(opts.asOf || '').trim() : '';
      if (asOf) {
        return getPropertyAsOf(scope, id, attrName, asOf);
      }
      return getProperty(scope, id, attrName);
    },
    setProperty: async (attrName, value, options) => setProperty(scope, id, attrName, value, options),
    linkEntities: async (relName, toId, props, options) =>
      linkEntities(scope, id, relName, toId, props, options),
    getNeighbors: async (relName, direction) => getNeighbors(scope, id, relName, direction),
  };
  Object.defineProperty(ctx, _BEHAVIOR_SCOPE, { value: scope });

  const resolvedMutations = [];
  for (const item of list) {
    const mutation = item && typeof item === 'object' ? String(item.mutation || '').trim() : '';
    const paramsObj = item && typeof item === 'object' ? (item.params || {}) : {};
    if (!mutation) throw new Error('Mutation item missing mutation name');

    const chain = [typeName, ...(await _getAncestorList(scope, typeName))];
    let resolved = null;
    for (const ownerType of chain) {
      if (await _mutationDefinitionExists(scope, ownerType, mutation)) {
        _ensureReadyIfBound(scope, 'mutation', ownerType, mutation, 'executor');
      }
      const map = scope.registrySnapshot.mutations.get(ownerType);
      const hit = map && map.get(mutation);
      if (hit) {
        resolved = hit;
        break;
      }
    }
    if (!resolved) {
      throw new Error(`Mutation '${mutation}' not defined for type '${typeName}'`);
    }
    resolvedMutations.push({ executor: resolved.executor, params: paramsObj });
  }

  for (const resolved of resolvedMutations) {
    await resolved.executor(ctx, resolved.params);
  }
}

async function executeMutations(db, entityId, mutations) {
  const scope = await _captureBehaviorResolutionScope(db);
  const rawDb = scope.runner;
  if (typeof rawDb.multiTransact !== 'function') {
    throw new Error('executeMutations requires a CozoDb instance with multiTransact(write)');
  }
  const tx = rawDb.multiTransact(true);
  try {
    await _executeMutationsInRunner(_scopeWithRunner(scope, tx), entityId, mutations);
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
  await _runDslCreateIgnoreConflict(
    runner,
    query().create(
      'om_behavior_binding',
      ['behavior_kind', 'owner_type', 'behavior_name', 'callback_slot', 'phase', 'seq'],
      ['binding_id']
    )
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
    const n = await _getNeighborsAtNormalizedAsOf(runner, fromId, relName, asOf);
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

function _isPermissionReference(ref, allowLiteral) {
  const value = String(ref || '').trim();
  if (!value) return false;
  if (value === 'subject.type' || value === 'resource.type') return true;
  if (value === 'subject.id' || value === 'action' || value === 'resource.id' || value === 'resource.field') return false;
  if (value.startsWith('subject.')) return value.length > 'subject.'.length;
  if (value.startsWith('resource.')) return value.length > 'resource.'.length;
  if (value.startsWith('field.')) return value.length > 'field.'.length;
  return allowLiteral;
}

function _describePermissionAbacShape(leftRef, op, rightRef) {
  const normalizedOp = String(op || '').trim();
  if (!['=', '==', '!=', '>', '>=', '<', '<=', 'hide'].includes(normalizedOp)) {
    return { valid: false, error: `Unsupported ABAC op '${normalizedOp}'` };
  }
  if (!_isPermissionReference(leftRef, false)
      || !_isPermissionReference(rightRef, true)
      || String(rightRef || '').trim().startsWith('field.')) {
    return { valid: false, error: 'malformed_reference' };
  }
  const isField = String(leftRef || '').trim().startsWith('field.');
  if ((normalizedOp === 'hide') !== isField) {
    return { valid: false, error: 'malformed_reference' };
  }
  return { valid: true, isHide: normalizedOp === 'hide' };
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
      ? await _getPropertyAtNormalizedAsOf(runner, entityId, canonicalAttr, asOf)
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
      // `[]` is the one valid zero-hop path: it witnesses only subject == resource.
      if (!rels.length && String(rawPath).trim() !== '[]') continue;
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
    const hiddenFields = [];
    for (const rule of abacRules) {
      const leftRef = rule.left_ref;
      const op = rule.op;
      const rightRef = rule.right_ref;
      const shape = _describePermissionAbacShape(leftRef, op, rightRef);

      if (!shape.valid) {
        abacOk = false;
        evalEntry.abac.rules.push({ leftRef, op, rightRef, result: false, error: shape.error });
        continue;
      }

      if (shape.isHide) {
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
          hiddenFields.push(field);
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
    if (pol.effect === 'allow') {
      for (const field of hiddenFields) {
        fieldVisibility[field] = 'hidden';
      }
    }
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

function _compareBehaviorSnapshotRows(left, right, keyColumns, numericColumns = []) {
  const numeric = new Set(numericColumns);
  for (const column of keyColumns) {
    const compared = numeric.has(column)
      ? Number(left[column]) - Number(right[column])
      : _ordinalCompare(left[column], right[column]);
    if (compared) return compared;
  }
  return 0;
}

function _sortBehaviorSnapshotRows(rows, keyColumns, numericColumns) {
  return [...rows].sort(
    (left, right) => _compareBehaviorSnapshotRows(
      left,
      right,
      keyColumns,
      numericColumns
    )
  );
}

async function _readBehaviorSnapshotParts(runner) {
  const readOptional = async (builder) => runDslRows(runner, builder).catch((e) => {
    if (_isStoredRelationMissingError(e)) return [];
    throw e;
  });

  const constraints = await readOptional(
    query()
      .select(['type_name', 'constraint_name', 'constraint_type', 'message'])
      .fromStored('om_constraint_def', {
        type_name: dsl.var('type_name'),
        constraint_name: dsl.var('constraint_name'),
        constraint_type: dsl.var('constraint_type'),
        message: dsl.var('message'),
      })
  );
  const computed = await readOptional(
    query()
      .select(['type_name', 'attr_name', 'description'])
      .fromStored('om_computed_def', {
        type_name: dsl.var('type_name'),
        attr_name: dsl.var('attr_name'),
        description: dsl.var('description'),
      })
  );
  const actions = await readOptional(
    query()
      .select(['type_name', 'action_name', 'description'])
      .fromStored('om_action_def', {
        type_name: dsl.var('type_name'),
        action_name: dsl.var('action_name'),
        description: dsl.var('description'),
      })
  );
  const mutations = await readOptional(
    query()
      .select(['type_name', 'mutation_name', 'description'])
      .fromStored('om_mutation_def', {
        type_name: dsl.var('type_name'),
        mutation_name: dsl.var('mutation_name'),
        description: dsl.var('description'),
      })
  );
  const interceptors = await readOptional(
    query()
      .select(['type_name', 'action_name', 'phase', 'seq', 'description'])
      .fromStored('om_interceptor_def', {
        type_name: dsl.var('type_name'),
        action_name: dsl.var('action_name'),
        phase: dsl.var('phase'),
        seq: dsl.var('seq'),
        description: dsl.var('description'),
      })
  );
  const bindings = await readOptional(
    query()
      .select([
        'behavior_kind',
        'owner_type',
        'behavior_name',
        'callback_slot',
        'phase',
        'seq',
        'binding_id',
      ])
      .fromStored('om_behavior_binding', {
        behavior_kind: dsl.var('behavior_kind'),
        owner_type: dsl.var('owner_type'),
        behavior_name: dsl.var('behavior_name'),
        callback_slot: dsl.var('callback_slot'),
        phase: dsl.var('phase'),
        seq: dsl.var('seq'),
        binding_id: dsl.var('binding_id'),
      })
  );

  return {
    formatVersion: 1,
    om_constraint_def: _sortBehaviorSnapshotRows(constraints, [0, 1]),
    om_computed_def: _sortBehaviorSnapshotRows(computed, [0, 1]),
    om_action_def: _sortBehaviorSnapshotRows(actions, [0, 1]),
    om_mutation_def: _sortBehaviorSnapshotRows(mutations, [0, 1]),
    om_interceptor_def: _sortBehaviorSnapshotRows(
      interceptors,
      [0, 1, 2, 3],
      [3]
    ),
    om_behavior_binding: _sortBehaviorSnapshotRows(
      bindings,
      [0, 1, 2, 3, 4, 5],
      [5]
    ),
  };
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

  const behavior = await _readBehaviorSnapshotParts(runner);

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
    behavior,
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
    behavior: parts.behavior,

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

const _BEHAVIOR_DIFF_RELATIONS = Object.freeze([
  Object.freeze({
    name: 'om_constraint_def',
    columns: Object.freeze(['type_name', 'constraint_name', 'constraint_type', 'message']),
    keyColumns: Object.freeze(['type_name', 'constraint_name']),
    numericKeyColumns: Object.freeze([]),
  }),
  Object.freeze({
    name: 'om_computed_def',
    columns: Object.freeze(['type_name', 'attr_name', 'description']),
    keyColumns: Object.freeze(['type_name', 'attr_name']),
    numericKeyColumns: Object.freeze([]),
  }),
  Object.freeze({
    name: 'om_action_def',
    columns: Object.freeze(['type_name', 'action_name', 'description']),
    keyColumns: Object.freeze(['type_name', 'action_name']),
    numericKeyColumns: Object.freeze([]),
  }),
  Object.freeze({
    name: 'om_mutation_def',
    columns: Object.freeze(['type_name', 'mutation_name', 'description']),
    keyColumns: Object.freeze(['type_name', 'mutation_name']),
    numericKeyColumns: Object.freeze([]),
  }),
  Object.freeze({
    name: 'om_interceptor_def',
    columns: Object.freeze(['type_name', 'action_name', 'phase', 'seq', 'description']),
    keyColumns: Object.freeze(['type_name', 'action_name', 'phase', 'seq']),
    numericKeyColumns: Object.freeze(['seq']),
  }),
  Object.freeze({
    name: 'om_behavior_binding',
    columns: Object.freeze([
      'behavior_kind',
      'owner_type',
      'behavior_name',
      'callback_slot',
      'phase',
      'seq',
      'binding_id',
    ]),
    keyColumns: Object.freeze([
      'behavior_kind',
      'owner_type',
      'behavior_name',
      'callback_slot',
      'phase',
      'seq',
    ]),
    numericKeyColumns: Object.freeze(['seq']),
  }),
]);

const _BEHAVIOR_STORED_RELATION_SCHEMAS = Object.freeze({
  om_constraint_def: Object.freeze({
    schemaText: 'type_name, constraint_name => constraint_type, message',
  }),
  om_computed_def: Object.freeze({
    schemaText: 'type_name, attr_name => description',
  }),
  om_action_def: Object.freeze({
    schemaText: 'type_name, action_name => description',
  }),
  om_mutation_def: Object.freeze({
    schemaText: 'type_name, mutation_name => description',
  }),
  om_interceptor_def: Object.freeze({
    schemaText: 'type_name, action_name, phase, seq => description',
  }),
  om_behavior_binding: Object.freeze({
    schemaText:
      'behavior_kind, owner_type, behavior_name, callback_slot, phase, seq => binding_id',
  }),
});

function _behaviorDiffRowObject(row, columns) {
  const value = {};
  for (let i = 0; i < columns.length; i++) value[columns[i]] = row[i];
  return value;
}

function _behaviorDiffKeyObject(row, relation) {
  const key = {};
  for (const column of relation.keyColumns) {
    key[column] = row[relation.columns.indexOf(column)];
  }
  return key;
}

function _behaviorDiffKeyIdentity(row, relation) {
  return JSON.stringify(
    relation.keyColumns.map((column) => row[relation.columns.indexOf(column)])
  );
}

function _compareBehaviorDiffRows(left, right, relation) {
  const numericColumns = new Set(relation.numericKeyColumns);
  for (const column of relation.keyColumns) {
    const index = relation.columns.indexOf(column);
    const compared = numericColumns.has(column)
      ? Number(left[index]) - Number(right[index])
      : _ordinalCompare(left[index], right[index]);
    if (compared) return compared;
  }
  return 0;
}

function _validateBehaviorDiffSide(section, side) {
  const diagnostics = [];
  const rowsByRelation = new Map();

  if (!section || typeof section !== 'object' || Array.isArray(section)) {
    diagnostics.push({
      code: 'OMSV1004',
      path: '$.behavior',
      message: `Invalid ${side} behavior snapshot section: expected an object.`,
      side,
      expectedFormatVersion: 1,
      actualFormatVersion: null,
    });
    return { diagnostics, rowsByRelation };
  }

  if (section.formatVersion !== 1) {
    diagnostics.push({
      code: 'OMSV1004',
      path: '$.behavior.formatVersion',
      message:
        `Unsupported formatVersion in ${side} behavior snapshot: `
        + `expected 1, received ${String(section.formatVersion)}.`,
      side,
      expectedFormatVersion: 1,
      actualFormatVersion: section.formatVersion,
    });
  }

  for (const relation of _BEHAVIOR_DIFF_RELATIONS) {
    const relationRows = section[relation.name];
    if (!Array.isArray(relationRows)) {
      diagnostics.push({
        code: 'OMSV1003',
        path: `$.behavior.${relation.name}`,
        message:
          `Invalid relation shape in ${side} behavior snapshot relation ${relation.name}: `
          + 'expected an array.',
        side,
        relation: relation.name,
        expectedColumns: relation.columns.length,
        actualColumns: null,
      });
      continue;
    }

    const validRows = [];
    const seenKeys = new Set();
    for (let index = 0; index < relationRows.length; index++) {
      const row = relationRows[index];
      if (!Array.isArray(row) || row.length !== relation.columns.length) {
        const actualColumns = Array.isArray(row) ? row.length : null;
        diagnostics.push({
          code: 'OMSV1003',
          path: `$.behavior.${relation.name}[${index}]`,
          message:
            `Invalid row shape in ${side} behavior snapshot relation ${relation.name}: `
            + `expected ${relation.columns.length} columns, received `
            + `${actualColumns == null ? 'a non-array value' : actualColumns}.`,
          side,
          relation: relation.name,
          expectedColumns: relation.columns.length,
          actualColumns,
        });
        continue;
      }

      const identity = _behaviorDiffKeyIdentity(row, relation);
      if (seenKeys.has(identity)) {
        diagnostics.push({
          code: 'OMSV1002',
          path: `$.behavior.${relation.name}[${index}]`,
          message:
            `Duplicate key in ${side} behavior snapshot relation ${relation.name}.`,
          side,
          relation: relation.name,
          key: _behaviorDiffKeyObject(row, relation),
        });
        continue;
      }

      seenKeys.add(identity);
      validRows.push(row);
    }
    rowsByRelation.set(relation.name, validRows);
  }

  return { diagnostics, rowsByRelation };
}

function _diffBehaviorRelation(fromRows, toRows, relation) {
  const fromByKey = new Map(
    fromRows.map((row) => [_behaviorDiffKeyIdentity(row, relation), row])
  );
  const toByKey = new Map(
    toRows.map((row) => [_behaviorDiffKeyIdentity(row, relation), row])
  );
  const allRowsByKey = new Map([...fromByKey, ...toByKey]);
  const sortedKeys = [...allRowsByKey.entries()]
    .sort((left, right) => _compareBehaviorDiffRows(left[1], right[1], relation))
    .map(([key]) => key);

  const added = [];
  const removed = [];
  const updated = [];

  for (const keyIdentity of sortedKeys) {
    const fromRow = fromByKey.get(keyIdentity);
    const toRow = toByKey.get(keyIdentity);
    if (!fromRow && toRow) {
      added.push(_behaviorDiffRowObject(toRow, relation.columns));
      continue;
    }
    if (fromRow && !toRow) {
      removed.push(_behaviorDiffRowObject(fromRow, relation.columns));
      continue;
    }
    if (!fromRow || !toRow) continue;

    if (JSON.stringify(fromRow) !== JSON.stringify(toRow)) {
      updated.push({
        key: _behaviorDiffKeyObject(toRow, relation),
        from: _behaviorDiffRowObject(fromRow, relation.columns),
        to: _behaviorDiffRowObject(toRow, relation.columns),
      });
    }
  }

  return { added, removed, updated };
}

function _diffBehaviorSnapshots(fromSnapshot, toSnapshot) {
  const fromPresent = Object.prototype.hasOwnProperty.call(fromSnapshot, 'behavior');
  const toPresent = Object.prototype.hasOwnProperty.call(toSnapshot, 'behavior');
  const result = {
    fromPresence: fromPresent ? 'present' : 'missing',
    toPresence: toPresent ? 'present' : 'missing',
    comparable: false,
    diagnostics: [],
    relations: {},
  };

  if (!fromPresent || !toPresent) {
    if (!fromPresent) {
      result.diagnostics.push({
        code: 'OMSV1001',
        path: '$.behavior',
        message: 'Cannot compare behavior schema: from snapshot is missing the behavior section.',
      });
    }
    if (!toPresent) {
      result.diagnostics.push({
        code: 'OMSV1001',
        path: '$.behavior',
        message: 'Cannot compare behavior schema: to snapshot is missing the behavior section.',
      });
    }
    return result;
  }

  const from = _validateBehaviorDiffSide(fromSnapshot.behavior, 'from');
  const to = _validateBehaviorDiffSide(toSnapshot.behavior, 'to');
  result.diagnostics.push(...from.diagnostics, ...to.diagnostics);
  if (result.diagnostics.length) return result;

  result.comparable = true;
  for (const relation of _BEHAVIOR_DIFF_RELATIONS) {
    result.relations[relation.name] = _diffBehaviorRelation(
      from.rowsByRelation.get(relation.name),
      to.rowsByRelation.get(relation.name),
      relation
    );
  }
  return result;
}

function _emptyBehaviorSnapshotSection() {
  const behavior = { formatVersion: 1 };
  for (const relation of _BEHAVIOR_DIFF_RELATIONS) {
    behavior[relation.name] = [];
  }
  return behavior;
}

function _legacyBehaviorMissingDiagnostic() {
  return {
    code: 'OMSV1001',
    path: '$.behavior',
    message:
      'Target schema snapshot is missing the behavior section; historical behavior facts are unknown.',
    allowedPolicies: ['preserve', 'clear'],
  };
}

function _normalizeLegacyBehaviorPolicy(options) {
  if (!options || !Object.prototype.hasOwnProperty.call(options, 'legacyBehaviorPolicy')) {
    return null;
  }
  const policy = options.legacyBehaviorPolicy;
  if (policy == null || policy === '') return null;
  if (policy === 'preserve' || policy === 'clear') return policy;
  throw new Error("legacyBehaviorPolicy must be either 'preserve' or 'clear'");
}

function _behaviorSnapshotSectionRows(section, side = 'target') {
  const validation = _validateBehaviorDiffSide(section, side);
  if (validation.diagnostics.length) {
    const first = validation.diagnostics[0];
    throw new Error(first.message || 'Invalid behavior snapshot section');
  }

  const behavior = { formatVersion: 1 };
  for (const relation of _BEHAVIOR_DIFF_RELATIONS) {
    behavior[relation.name] = _sortBehaviorSnapshotRows(
      validation.rowsByRelation.get(relation.name) || [],
      relation.keyColumns.map((column) => relation.columns.indexOf(column)),
      relation.numericKeyColumns.map((column) => relation.columns.indexOf(column))
    );
  }
  return behavior;
}

function _snapshotWithEffectiveBehavior(snapshot, behavior) {
  const effective = JSON.parse(JSON.stringify(snapshot));
  effective.behavior = behavior;
  return effective;
}

async function _replaceBehaviorSnapshotRows(runner, behavior) {
  const section = _behaviorSnapshotSectionRows(behavior, 'target');
  for (const relation of _BEHAVIOR_DIFF_RELATIONS) {
    const schema = _BEHAVIOR_STORED_RELATION_SCHEMAS[relation.name];
    await _replaceStoredRelation(
      runner,
      relation.name,
      relation.columns,
      schema.schemaText,
      section[relation.name]
    );
  }
  return section;
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
  const snapshotJson = JSON.stringify(snapshot);

  await runDslRows(
    runner,
    query()
      .input({
        version: param('version', v),
        snapshot_json: param('snapshot_json', snapshotJson),
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
    behavior: _diffBehaviorSnapshots(fromSnapshot, toSnapshot),
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
      const fromSnapshotJsonText = JSON.stringify(fromSnapshotJson);
      await runDslRows(
        txRunner,
        query()
          .input({
            version: param('version', fromVersion),
            snapshot_json: param('snapshot_json', fromSnapshotJsonText),
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
    const snapshotJsonText = JSON.stringify(snapshotJson);
    const checksum = _computeSchemaChecksum(snapshotJsonText);

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
          snapshot_json: param('snapshot_json', snapshotJsonText),
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

async function _rollbackSchemaInsideBehaviorGate(runner, targetVersion, options, cacheRunner) {
  const tv = Number(targetVersion);
  if (!Number.isFinite(tv) || tv <= 0) throw new Error('targetVersion must be a positive number');

  const opts = options && typeof options === 'object' ? options : {};
  const strict = opts.strict !== false;
  const legacyBehaviorPolicy = _normalizeLegacyBehaviorPolicy(opts);

  // Read current state outside the transaction for stable return fields.
  const state0 = await getSchemaState(runner);
  const fromVersion = Number(state0.currentVersion);

  if (fromVersion === tv) {
    // Still invalidate alias caches so callers see any out-of-band alias changes.
    try {
      _aliasCacheByRunner.delete(cacheRunner || runner);
    } catch (_) {
    }
    return {
      ok: true,
      strict,
      targetVersion: tv,
      fromVersion,
      diagnostics: [],
      compatibilityDiagnostics: [],
      behaviorPolicyApplied: null,
    };
  }

  const snapshot = await readSchemaSnapshot(runner, tv);
  if (!snapshot) {
    throw new Error(`Missing schema snapshot for version=${tv}; cannot rollback`);
  }

  const behaviorPresent = Object.prototype.hasOwnProperty.call(snapshot, 'behavior');
  const compatibilityDiagnostics = behaviorPresent ? [] : [_legacyBehaviorMissingDiagnostic()];
  if (!behaviorPresent && !legacyBehaviorPolicy) {
    return {
      ok: false,
      strict,
      targetVersion: tv,
      fromVersion,
      diagnostics: [],
      compatibilityDiagnostics,
      behaviorPolicyApplied: null,
    };
  }

  const result = await _withWriteTxIfPossible(runner, async (txRunner) => {
    let behaviorPolicyApplied = 'snapshot';
    let effectiveBehavior = null;
    if (behaviorPresent) {
      effectiveBehavior = _behaviorSnapshotSectionRows(snapshot.behavior, 'target');
    } else if (legacyBehaviorPolicy === 'preserve') {
      behaviorPolicyApplied = 'preserve';
      effectiveBehavior = await _readBehaviorSnapshotParts(txRunner);
    } else if (legacyBehaviorPolicy === 'clear') {
      behaviorPolicyApplied = 'clear';
      effectiveBehavior = _emptyBehaviorSnapshotSection();
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

    if (behaviorPresent) {
      effectiveBehavior = await _replaceBehaviorSnapshotRows(txRunner, snapshot.behavior);
    } else if (legacyBehaviorPolicy === 'clear') {
      effectiveBehavior = await _replaceBehaviorSnapshotRows(txRunner, effectiveBehavior);
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
      const v = await _validateEntityShapeOnly(txRunner, id);
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
    const checksum = _computeSchemaChecksum(
      behaviorPresent
        ? snapshot
        : _snapshotWithEffectiveBehavior(snapshot, effectiveBehavior)
    );
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

    return {
      ok: true,
      strict,
      targetVersion: tv,
      fromVersion,
      diagnostics,
      compatibilityDiagnostics,
      behaviorPolicyApplied,
    };
  });

  // Invalidate alias caches for the original runner so subsequent reads resolve fresh.
  try {
    _aliasCacheByRunner.delete(cacheRunner || runner);
  } catch (_) {
  }

  return result;
}

async function rollbackSchema(runner, targetVersion, options) {
  return _withBehaviorGate(runner, async () => {
    const scope = _captureResolutionScope(runner);
    return _rollbackSchemaInsideBehaviorGate(scope, targetVersion, options, runner);
  });
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
  runner = await _captureBehaviorResolutionScope(runner);
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

  let storedValue = primaryRows.length ? primaryRows[0][0] : undefined;

  if (storedValue === undefined) {
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
        storedValue = rows[0][0];
        break;
      }
    }
  }

  const chosen = await _resolveComputedCallback(runner, typeName, canonicalAttrName);
  if (storedValue !== undefined) return storedValue;
  if (!chosen) {
    return undefined;
  }

  const ctx = {
    runner: runner.runner,
    runtime: _runtimeForScope(runner),
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
  runner = await _captureBehaviorResolutionScope(runner);
  const id = String(entityId || '').trim();
  const an = String(attrName || '').trim();
  if (!id) throw new Error('entityId is required');
  if (!an) throw new Error('attrName is required');
  const asOf = _normalizeAsOfTimestamp(timestamp, 'timestamp');

  return _getPropertyAtNormalizedAsOf(runner, id, an, asOf);
}

async function _getPropertyAtNormalizedAsOf(runner, id, an, asOf) {
  runner = await _captureBehaviorResolutionScope(runner);
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
  let storedValue = rows.length ? rows[0][0] : undefined;

  if (storedValue === undefined) {
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
        storedValue = hit[0][0];
        break;
      }
    }
  }

  const chosen = await _resolveComputedCallback(runner, typeName, canonicalAttrName);
  if (storedValue !== undefined) return storedValue;
  if (!chosen) {
    return undefined;
  }

  const ctx = {
    runner: runner.runner,
    runtime: _runtimeForScope(runner),
    entityId: id,
    typeName,
    asOf,
    getProperty: async (name) => _getPropertyAtNormalizedAsOf(runner, id, name, asOf),
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

async function _validateEntityShapeOnly(runner, entityId) {
  const errors = [];
  const typeName = await getEntityType(runner, entityId);
  const exists = await _typeExists(runner, typeName);
  if (!exists) {
    errors.push(`Unknown type '${typeName}'`);
  }
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

async function validateEntity(runner, entityId) {
  const shapeResult = await _validateEntityShapeOnly(runner, entityId);
  const errors = [...shapeResult.errors];

  const constraintResult = await validateConstraints(runner, entityId);
  errors.push(...constraintResult.errors);

  return { valid: errors.length === 0, errors };
}

async function finalizeEntity(runner, entityId) {
  const result = await validateEntity(runner, entityId);
  if (!result.valid) {
    throw new Error(`Entity '${entityId}' validation failed: ${result.errors.join('; ')}`);
  }
}

async function getEntityView(runner, entityId) {
  runner = await _captureBehaviorResolutionScope(runner);
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

  const computed = await _listEffectiveComputedDefinitions(runner, typeName);
  for (const { attrName } of computed) {
    const canonicalAttrName = await _resolveAttrForCanonicalType(runner, typeName, attrName);
    try {
      await _resolveComputedCallback(runner, typeName, canonicalAttrName);
      if (canonicalAttrName in properties) continue;
      const v = await getProperty(runner, entityId, canonicalAttrName);
      if (v !== undefined) {
        properties[canonicalAttrName] = v;
      }
    } catch (error) {
      if (error instanceof BehaviorUnresolvedError) throw error;
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
  runner = await _captureBehaviorResolutionScope(runner);
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

  const computed = await _listEffectiveComputedDefinitions(runner, typeName);
  for (const { attrName } of computed) {
    const canonicalAttrName = await _resolveAttrForCanonicalType(runner, typeName, attrName);
    try {
      await _resolveComputedCallback(runner, typeName, canonicalAttrName);
      if (canonicalAttrName in properties) continue;
      const v = await getPropertyAsOf(runner, id, canonicalAttrName, asOf);
      if (v !== undefined) {
        properties[canonicalAttrName] = v;
      }
    } catch (error) {
      if (error instanceof BehaviorUnresolvedError) throw error;
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

  return _getNeighborsAtNormalizedAsOf(runner, id, relName, asOf);
}

async function _getNeighborsAtNormalizedAsOf(runner, id, relName, asOf) {

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
  const scope = _captureResolutionScope(db);
  const rawDb = scope.runner;
  if (typeof rawDb.multiTransact !== 'function') {
    throw new Error('ingestBatch requires a CozoDb instance with multiTransact(write)');
  }

  const entities = Array.isArray(batch && batch.entities) ? batch.entities : [];
  const properties = Array.isArray(batch && batch.properties) ? batch.properties : [];
  const edges = Array.isArray(batch && batch.edges) ? batch.edges : [];
  const touchedEntityIds = new Set();
  const tx = rawDb.multiTransact(true);
  const txScope = _scopeWithRunner(scope, tx);

  try {
    for (const entity of entities) {
      await createEntity(txScope, entity.id, entity.typeName, entity.label);
      touchedEntityIds.add(entity.id);
    }

    for (const property of properties) {
      await setProperty(txScope, property.entityId, property.attrName, property.value);
      touchedEntityIds.add(property.entityId);
    }

    for (const edge of edges) {
      await linkEntities(txScope, edge.fromId, edge.relName, edge.toId, edge.props || {});
    }

    if (options.validateRequired !== false) {
      for (const entityId of touchedEntityIds) {
        await finalizeEntity(txScope, entityId);
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
  createOmRuntime,
  BehaviorUnresolvedError,
  BehaviorImportError,
  registerConstraint,
  registerValidator,
  registerComputed,
  registerAction,
  registerMutation,
  registerInterceptor,
  initSchema,
  createSchema,
  getBehaviorCatalog,
  encodeBehaviorManifestJson,
  decodeBehaviorManifestJson,
  exportBehaviorManifestJson,
  importBehaviorManifestJson,
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
