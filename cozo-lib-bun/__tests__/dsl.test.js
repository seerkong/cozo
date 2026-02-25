const { describe, test, expect } = require('bun:test');
const {
  assertIdentifier,
  DslError,
  query,
  variable,
  param,
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
  inExpr,
} = require('../cozo-dsl');

// ---------------------------------------------------------------------------
// assertIdentifier
// ---------------------------------------------------------------------------
describe('assertIdentifier', () => {
  test('accepts simple identifiers', () => {
    expect(assertIdentifier('foo')).toBe('foo');
    expect(assertIdentifier('_bar')).toBe('_bar');
    expect(assertIdentifier('A1_b2')).toBe('A1_b2');
  });

  test('rejects non-string input', () => {
    expect(() => assertIdentifier(123)).toThrow(DslError);
    expect(() => assertIdentifier(null)).toThrow(DslError);
    expect(() => assertIdentifier(undefined)).toThrow(DslError);
  });

  test('rejects strings that are not valid identifiers', () => {
    expect(() => assertIdentifier('')).toThrow(DslError);
    expect(() => assertIdentifier('1abc')).toThrow(DslError);
    expect(() => assertIdentifier('foo bar')).toThrow(DslError);
    expect(() => assertIdentifier('foo-bar')).toThrow(DslError);
    expect(() => assertIdentifier('foo.bar')).toThrow(DslError);
  });

  test('error includes DSL101 code', () => {
    try {
      assertIdentifier('1bad');
      throw new Error('should have thrown');
    } catch (e) {
      expect(e.code).toBe('DSL101');
    }
  });
});

// ---------------------------------------------------------------------------
// query builder basics: input / select / fromStored
// ---------------------------------------------------------------------------
describe('query builder - input', () => {
  test('single input clause', () => {
    const { script, params } = query()
      .input({ name: 'Alice', age: 30 })
      .select(['name', 'age'])
      .atom('some_rule(name, age)')
      .build();

    expect(script).toContain('?[name, age] <- [[');
    expect(params._p0).toBe('Alice');
    expect(params._p1).toBe(30);
  });

  test('input with param token', () => {
    const { script, params } = query()
      .input({ id: param('myId', 42) })
      .select(['id'])
      .atom('r(id)')
      .build();

    expect(script).toContain('$myId');
    expect(params.myId).toBe(42);
  });

  test('input with variable token', () => {
    const { script } = query()
      .input({ x: variable('x') })
      .select(['x'])
      .atom('r(x)')
      .build();

    // variable compiles to bare name, not a param reference
    expect(script).toContain('<- [[x]]');
  });

  test('input rejects empty object', () => {
    expect(() => query().input({})).toThrow(DslError);
  });

  test('input rejects non-object', () => {
    expect(() => query().input('bad')).toThrow(DslError);
    expect(() => query().input(null)).toThrow(DslError);
    expect(() => query().input([])).toThrow(DslError);
  });
});

describe('query builder - select', () => {
  test('select sets head variables', () => {
    const { script } = query()
      .select(['a', 'b'])
      .atom('r(a, b)')
      .build();

    expect(script).toContain('?[a, b] :=');
  });

  test('select rejects empty array', () => {
    expect(() => query().select([])).toThrow(DslError);
  });

  test('select rejects non-array', () => {
    expect(() => query().select('bad')).toThrow(DslError);
  });
});

describe('query builder - fromStored', () => {
  test('generates stored relation atom', () => {
    const { script } = query()
      .select(['name', 'age'])
      .fromStored('person', { name: variable('name'), age: variable('age') })
      .build();

    expect(script).toContain('*person{name: name, age: age}');
  });

  test('fromStored with param values', () => {
    const { script, params } = query()
      .select(['name'])
      .fromStored('person', { name: variable('name'), status: 'active' })
      .build();

    expect(script).toContain('*person{');
    expect(script).toContain('status: $_p0');
    expect(params._p0).toBe('active');
  });

  test('fromStored rejects invalid relation name', () => {
    expect(() => query().fromStored('1bad', { a: variable('a') })).toThrow(DslError);
  });

  test('fromStored rejects empty bindings', () => {
    expect(() => query().fromStored('rel', {})).toThrow(DslError);
  });

  test('fromStored rejects non-object bindings', () => {
    expect(() => query().fromStored('rel', null)).toThrow(DslError);
    expect(() => query().fromStored('rel', 'bad')).toThrow(DslError);
  });
});

// ---------------------------------------------------------------------------
// where
// ---------------------------------------------------------------------------
describe('query builder - where', () => {
  test('where with eq expression', () => {
    const { script, params } = query()
      .select(['name'])
      .fromStored('person', { name: variable('name'), age: variable('age') })
      .where(eq(variable('age'), 30))
      .build();

    expect(script).toContain('(age == $_p0)');
    expect(params._p0).toBe(30);
  });

  test('where rejects null', () => {
    expect(() => query().where(null)).toThrow(DslError);
  });

  test('where rejects undefined', () => {
    expect(() => query().where(undefined)).toThrow(DslError);
  });
});

// ---------------------------------------------------------------------------
// order / limit / offset
// ---------------------------------------------------------------------------
describe('query builder - order/limit/offset', () => {
  test('order adds :order directive', () => {
    const { script } = query()
      .select(['a'])
      .atom('r(a)')
      .order('a')
      .build();

    expect(script).toContain(':order a');
  });

  test('multiple order columns', () => {
    const { script } = query()
      .select(['a', 'b'])
      .atom('r(a, b)')
      .order('a', 'b')
      .build();

    expect(script).toContain(':order a, b');
  });

  test('limit adds :limit directive', () => {
    const { script } = query()
      .select(['a'])
      .atom('r(a)')
      .limit(10)
      .build();

    expect(script).toContain(':limit 10');
  });

  test('offset adds :offset directive', () => {
    const { script } = query()
      .select(['a'])
      .atom('r(a)')
      .offset(5)
      .build();

    expect(script).toContain(':offset 5');
  });

  test('order + limit + offset together', () => {
    const { script } = query()
      .select(['x'])
      .atom('r(x)')
      .order('x')
      .limit(10)
      .offset(20)
      .build();

    expect(script).toContain(':order x');
    expect(script).toContain(':limit 10');
    expect(script).toContain(':offset 20');
  });

  test('limit rejects negative', () => {
    expect(() => query().limit(-1)).toThrow(DslError);
  });

  test('limit rejects non-integer', () => {
    expect(() => query().limit(1.5)).toThrow(DslError);
  });

  test('offset rejects negative', () => {
    expect(() => query().offset(-1)).toThrow(DslError);
  });
});

// ---------------------------------------------------------------------------
// build - empty query
// ---------------------------------------------------------------------------
describe('query builder - build', () => {
  test('build throws on empty query', () => {
    expect(() => query().build()).toThrow(DslError);
    try {
      query().build();
    } catch (e) {
      expect(e.code).toBe('DSL115');
    }
  });

  test('select without atoms throws', () => {
    expect(() => query().select(['a']).build()).toThrow(DslError);
  });
});

// ---------------------------------------------------------------------------
// expression helpers
// ---------------------------------------------------------------------------
describe('expression helpers', () => {
  test('eq produces == comparison', () => {
    const { script } = query()
      .select(['x'])
      .atom('r(x)')
      .where(eq(variable('x'), 1))
      .build();

    expect(script).toContain('(x == $_p0)');
  });

  test('neq produces != comparison', () => {
    const { script } = query()
      .select(['x'])
      .atom('r(x)')
      .where(neq(variable('x'), 2))
      .build();

    expect(script).toContain('(x != $_p0)');
  });

  test('gt produces > comparison', () => {
    const { script } = query()
      .select(['x'])
      .atom('r(x)')
      .where(gt(variable('x'), 3))
      .build();

    expect(script).toContain('(x > $_p0)');
  });

  test('lt produces < comparison', () => {
    const { script } = query()
      .select(['x'])
      .atom('r(x)')
      .where(lt(variable('x'), 4))
      .build();

    expect(script).toContain('(x < $_p0)');
  });

  test('gte produces >= comparison', () => {
    const { script } = query()
      .select(['x'])
      .atom('r(x)')
      .where(gte(variable('x'), 5))
      .build();

    expect(script).toContain('(x >= $_p0)');
  });

  test('lte produces <= comparison', () => {
    const { script } = query()
      .select(['x'])
      .atom('r(x)')
      .where(lte(variable('x'), 6))
      .build();

    expect(script).toContain('(x <= $_p0)');
  });

  test('and combines with &&', () => {
    const { script } = query()
      .select(['x'])
      .atom('r(x)')
      .where(and(gt(variable('x'), 0), lt(variable('x'), 100)))
      .build();

    expect(script).toContain('&&');
    expect(script).toContain('(x > $_p0)');
    expect(script).toContain('(x < $_p1)');
  });

  test('or combines with ||', () => {
    const { script } = query()
      .select(['x'])
      .atom('r(x)')
      .where(or(eq(variable('x'), 1), eq(variable('x'), 2)))
      .build();

    expect(script).toContain('||');
  });

  test('not wraps with !', () => {
    const { script } = query()
      .select(['x'])
      .atom('r(x)')
      .where(not(eq(variable('x'), 0)))
      .build();

    expect(script).toContain('(!(x == $_p0))');
  });

  test('inExpr produces is_in()', () => {
    const { script, params } = query()
      .select(['x'])
      .atom('r(x)')
      .where(inExpr(variable('x'), [1, 2, 3]))
      .build();

    expect(script).toContain('is_in(x, [$_p0, $_p1, $_p2])');
    expect(params._p0).toBe(1);
    expect(params._p1).toBe(2);
    expect(params._p2).toBe(3);
  });
});

// ---------------------------------------------------------------------------
// expression error cases
// ---------------------------------------------------------------------------
describe('expression error cases', () => {
  test('and() with no args throws', () => {
    expect(() => and()).toThrow(DslError);
  });

  test('and() with only null/undefined throws', () => {
    expect(() => and(null, undefined)).toThrow(DslError);
  });

  test('or() with no args throws', () => {
    expect(() => or()).toThrow(DslError);
  });

  test('not(null) throws', () => {
    expect(() => not(null)).toThrow(DslError);
  });

  test('not(undefined) throws', () => {
    expect(() => not(undefined)).toThrow(DslError);
  });

  test('inExpr with empty array throws', () => {
    expect(() => inExpr(variable('x'), [])).toThrow(DslError);
  });

  test('inExpr with non-array throws', () => {
    expect(() => inExpr(variable('x'), 'bad')).toThrow(DslError);
  });
});

// ---------------------------------------------------------------------------
// createRaw directive
// ---------------------------------------------------------------------------
describe('createRaw', () => {
  test('generates :create directive with schema', () => {
    const { script } = query()
      .input({ id: 1, name: 'test' })
      .createRaw('my_table', 'id: Int => name: String')
      .build();

    expect(script).toContain(':create my_table {id: Int => name: String}');
  });

  test('rejects invalid relation name', () => {
    expect(() => query().createRaw('1bad', 'id: Int')).toThrow(DslError);
  });

  test('rejects empty schema', () => {
    expect(() => query().createRaw('tbl', '')).toThrow(DslError);
    expect(() => query().createRaw('tbl', null)).toThrow(DslError);
  });
});

// ---------------------------------------------------------------------------
// val() and raw() token helpers
// ---------------------------------------------------------------------------
describe('val and raw tokens', () => {
  test('val() auto-assigns parameter', () => {
    const { script, params } = query()
      .input({ x: val(99) })
      .select(['x'])
      .atom('r(x)')
      .build();

    // val creates an auto-param
    const paramKeys = Object.keys(params);
    expect(paramKeys.length).toBeGreaterThanOrEqual(1);
    expect(Object.values(params)).toContain(99);
  });

  test('raw() injects text verbatim', () => {
    const { script } = query()
      .input({ x: raw('now()') })
      .select(['x'])
      .atom('r(x)')
      .build();

    expect(script).toContain('now()');
  });
});
