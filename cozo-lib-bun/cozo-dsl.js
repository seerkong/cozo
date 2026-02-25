const IDENTIFIER_RE = /^[A-Za-z_][A-Za-z0-9_]*$/;

class DslError extends Error {
  constructor(code, message) {
    super(`${code}: ${message}`);
    this.name = 'DslError';
    this.code = code;
  }
}

class VarToken {
  constructor(name) {
    this.name = assertIdentifier(name, 'variable');
  }
}

class ParamToken {
  constructor(name, value) {
    this.name = assertIdentifier(name, 'parameter');
    this.value = value;
  }
}

class RawToken {
  constructor(text) {
    this.text = String(text || '');
  }
}

class AutoParamToken {
  constructor(value) {
    this.value = value;
  }
}

function assertIdentifier(identifier, kind = 'identifier') {
  if (typeof identifier !== 'string' || !IDENTIFIER_RE.test(identifier)) {
    throw new DslError('DSL101', `Invalid ${kind}: '${identifier}'`);
  }
  return identifier;
}

function variable(name) {
  return new VarToken(name);
}

function param(name, value) {
  return new ParamToken(name, value);
}

const p = param;

function val(value) {
  return new AutoParamToken(value);
}

function raw(text) {
  return new RawToken(text);
}

function makeExpr(kind, payload) {
  return {
    __dslExpr: true,
    kind,
    ...payload,
  };
}

function isExprNode(value) {
  return !!value && typeof value === 'object' && value.__dslExpr === true;
}

function cmp(op, left, right) {
  const supported = new Set(['==', '!=', '>', '<', '>=', '<=']);
  if (!supported.has(op)) {
    throw new DslError('DSL117', `Unsupported comparator '${op}'`);
  }
  return makeExpr('cmp', { op, left, right });
}

function eq(left, right) {
  return cmp('==', left, right);
}

function neq(left, right) {
  return cmp('!=', left, right);
}

function gt(left, right) {
  return cmp('>', left, right);
}

function lt(left, right) {
  return cmp('<', left, right);
}

function gte(left, right) {
  return cmp('>=', left, right);
}

function lte(left, right) {
  return cmp('<=', left, right);
}

function and(...children) {
  const normalized = children.filter((child) => child !== undefined && child !== null);
  if (!normalized.length) {
    throw new DslError('DSL118', 'and() expects at least one condition');
  }
  return makeExpr('and', { children: normalized });
}

function or(...children) {
  const normalized = children.filter((child) => child !== undefined && child !== null);
  if (!normalized.length) {
    throw new DslError('DSL119', 'or() expects at least one condition');
  }
  return makeExpr('or', { children: normalized });
}

function inExpr(left, values) {
  if (!Array.isArray(values) || !values.length) {
    throw new DslError('DSL120', 'in() expects a non-empty values array');
  }
  return makeExpr('in', { left, values });
}

function not(child) {
  if (child === undefined || child === null) {
    throw new DslError('DSL125', 'not() expects one condition');
  }
  return makeExpr('not', { child });
}

function isPlainObject(value) {
  return !!value && typeof value === 'object' && !Array.isArray(value);
}

class CozoQueryBuilder {
  constructor() {
    this.inputClauses = [];
    this.head = null;
    this.atoms = [];
    this.conditions = [];
    this.directives = [];
    this.orders = [];
    this.limitValue = null;
    this.offsetValue = null;
    this.params = {};
    this.autoParamCounter = 0;
  }

  _nextAutoParamName() {
    const name = `_p${this.autoParamCounter}`;
    this.autoParamCounter += 1;
    return name;
  }

  _registerParam(name, value) {
    assertIdentifier(name, 'parameter');
    if (Object.prototype.hasOwnProperty.call(this.params, name) && !Object.is(this.params[name], value)) {
      throw new DslError('DSL201', `Parameter '${name}' assigned conflicting values`);
    }
    this.params[name] = value;
    return `$${name}`;
  }

  _compileTerm(term) {
    if (term instanceof VarToken) {
      return term.name;
    }
    if (term instanceof ParamToken) {
      return this._registerParam(term.name, term.value);
    }
    if (term instanceof AutoParamToken) {
      const autoName = this._nextAutoParamName();
      return this._registerParam(autoName, term.value);
    }
    if (term instanceof RawToken) {
      return term.text;
    }
    if (typeof term === 'string') {
      return this._registerParam(this._nextAutoParamName(), term);
    }
    if (typeof term === 'number' || typeof term === 'boolean' || term === null) {
      return this._registerParam(this._nextAutoParamName(), term);
    }
    if (isPlainObject(term) || Array.isArray(term)) {
      return this._registerParam(this._nextAutoParamName(), term);
    }
    throw new DslError('DSL102', `Unsupported term type: ${typeof term}`);
  }

  input(bindings) {
    if (!isPlainObject(bindings) || !Object.keys(bindings).length) {
      throw new DslError('DSL103', 'input(bindings) expects a non-empty object');
    }

    const columns = Object.keys(bindings).map((column) => assertIdentifier(column, 'column'));
    const values = columns.map((column) => this._compileTerm(bindings[column]));
    this.inputClauses.push(`?[${columns.join(', ')}] <- [[${values.join(', ')}]]`);
    return this;
  }

  select(columns) {
    if (!Array.isArray(columns) || !columns.length) {
      throw new DslError('DSL104', 'select(columns) expects a non-empty array');
    }
    this.head = columns.map((column) => assertIdentifier(column, 'head variable'));
    return this;
  }

  fromStored(relationName, bindings) {
    assertIdentifier(relationName, 'relation');
    if (!isPlainObject(bindings) || !Object.keys(bindings).length) {
      throw new DslError('DSL105', 'fromStored(name, bindings) expects a non-empty bindings object');
    }

    const orderedKeys = Object.keys(bindings).map((key) => assertIdentifier(key, 'relation field'));
    const fields = orderedKeys.map((key) => `${key}: ${this._compileTerm(bindings[key])}`);
    this.atoms.push(`*${relationName}{${fields.join(', ')}}`);
    return this;
  }

  atom(text) {
    const value = String(text || '').trim();
    if (!value) {
      throw new DslError('DSL106', 'atom(text) expects non-empty text');
    }
    this.atoms.push(value);
    return this;
  }

  whereEq(leftVarName, rightTerm) {
    this.where(eq(variable(leftVarName), rightTerm));
    return this;
  }

  where(condition) {
    if (condition === undefined || condition === null) {
      throw new DslError('DSL121', 'where(condition) expects a condition');
    }
    this.conditions.push(condition);
    return this;
  }

  _compileExprLeft(term) {
    if (term instanceof VarToken) {
      return term.name;
    }
    if (typeof term === 'string') {
      return assertIdentifier(term, 'variable');
    }
    if (term instanceof RawToken) {
      return term.text;
    }
    return this._compileTerm(term);
  }

  _compileExpression(node) {
    if (node instanceof RawToken) {
      return node.text;
    }
    if (typeof node === 'string') {
      return node;
    }
    if (!isExprNode(node)) {
      throw new DslError('DSL122', 'where() condition must be an expression node');
    }

    if (node.kind === 'cmp') {
      const left = this._compileExprLeft(node.left);
      const right = this._compileTerm(node.right);
      return `(${left} ${node.op} ${right})`;
    }

    if (node.kind === 'in') {
      const left = this._compileExprLeft(node.left);
      const values = node.values.map((entry) => this._compileTerm(entry));
      return `is_in(${left}, [${values.join(', ')}])`;
    }

    if (node.kind === 'and' || node.kind === 'or') {
      const operator = node.kind === 'and' ? '&&' : '||';
      if (!Array.isArray(node.children) || !node.children.length) {
        throw new DslError('DSL123', `${node.kind}() must contain at least one child`);
      }
      const children = node.children.map((child) => this._compileExpression(child));
      return `(${children.join(` ${operator} `)})`;
    }

    if (node.kind === 'not') {
      const child = this._compileExpression(node.child);
      return `(!${child})`;
    }

    throw new DslError('DSL124', `Unsupported expression kind '${node.kind}'`);
  }

  mutation(kind, relationName, keyColumns, valueColumns = []) {
    const normalizedKind = String(kind || '').toLowerCase();
    const allowedKinds = new Set(['put', 'insert', 'update', 'rm', 'create']);
    if (!allowedKinds.has(normalizedKind)) {
      throw new DslError('DSL107', `Unsupported mutation kind '${kind}'`);
    }

    const relation = assertIdentifier(relationName, 'relation');
    if (!Array.isArray(keyColumns) || !keyColumns.length) {
      throw new DslError('DSL108', `${normalizedKind} requires non-empty key columns`);
    }

    const keys = keyColumns.map((column) => assertIdentifier(column, 'key column'));
    const values = Array.isArray(valueColumns)
      ? valueColumns.map((column) => assertIdentifier(column, 'value column'))
      : [];

    const schema = values.length
      ? `{${keys.join(', ')} => ${values.join(', ')}}`
      : `{${keys.join(', ')}}`;
    this.directives.push(`:${normalizedKind} ${relation} ${schema}`);
    return this;
  }

  put(relationName, keyColumns, valueColumns = []) {
    return this.mutation('put', relationName, keyColumns, valueColumns);
  }

  insert(relationName, keyColumns, valueColumns = []) {
    return this.mutation('insert', relationName, keyColumns, valueColumns);
  }

  update(relationName, keyColumns, valueColumns = []) {
    return this.mutation('update', relationName, keyColumns, valueColumns);
  }

  rm(relationName, keyColumns) {
    return this.mutation('rm', relationName, keyColumns, []);
  }

  create(relationName, keyColumns, valueColumns = []) {
    return this.mutation('create', relationName, keyColumns, valueColumns);
  }

  createRaw(relationName, schemaText) {
    const relation = assertIdentifier(relationName, 'relation');
    const schema = String(schemaText || '').trim();
    if (!schema) {
      throw new DslError('DSL109', 'createRaw requires non-empty schema text');
    }
    this.directives.push(`:create ${relation} {${schema}}`);
    return this;
  }

  order(...columns) {
    if (!columns.length) {
      throw new DslError('DSL110', 'order() expects at least one column');
    }
    for (const column of columns) {
      this.orders.push(assertIdentifier(column, 'order column'));
    }
    return this;
  }

  limit(value) {
    if (!Number.isInteger(value) || value < 0) {
      throw new DslError('DSL111', `Invalid limit value '${value}'`);
    }
    this.limitValue = value;
    return this;
  }

  offset(value) {
    if (!Number.isInteger(value) || value < 0) {
      throw new DslError('DSL112', `Invalid offset value '${value}'`);
    }
    this.offsetValue = value;
    return this;
  }

  rawDirective(text) {
    const value = String(text || '').trim();
    if (!value) {
      throw new DslError('DSL113', 'rawDirective(text) expects non-empty text');
    }
    this.directives.push(value);
    return this;
  }

  build() {
    const lines = [];
    const atoms = [...this.atoms];

    for (const clause of this.inputClauses) {
      lines.push(clause);
    }

    for (const condition of this.conditions) {
      atoms.push(this._compileExpression(condition));
    }

    if (this.head) {
      if (!atoms.length) {
        throw new DslError('DSL114', 'select() requires at least one atom');
      }
      lines.push(`?[${this.head.join(', ')}] :=\n  ${atoms.join(',\n  ')}`);
    }

    for (const directive of this.directives) {
      lines.push(directive);
    }

    if (this.orders.length) {
      lines.push(`:order ${this.orders.join(', ')}`);
    }
    if (this.offsetValue !== null) {
      lines.push(`:offset ${this.offsetValue}`);
    }
    if (this.limitValue !== null) {
      lines.push(`:limit ${this.limitValue}`);
    }

    if (!lines.length) {
      throw new DslError('DSL115', 'Nothing to build; query is empty');
    }

    return {
      script: lines.join('\n'),
      params: { ...this.params },
    };
  }

  async execute(runner) {
    if (!runner || typeof runner.run !== 'function') {
      throw new DslError('DSL116', 'execute(runner) requires runner.run(script, params)');
    }
    const { script, params } = this.build();
    return runner.run(script, params);
  }
}

function query() {
  return new CozoQueryBuilder();
}

module.exports = {
  DslError,
  CozoQueryBuilder,
  query,
  var: variable,
  variable,
  param,
  p,
  val,
  raw,
  eq,
  neq,
  gt,
  lt,
  gte,
  lte,
  and,
  or,
  not,
  in: inExpr,
  inExpr,
  assertIdentifier,
};
