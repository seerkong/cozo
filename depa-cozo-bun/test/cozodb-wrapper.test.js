const { expect, test, describe, beforeEach, afterEach } = require('bun:test');
const os = require('os');
const path = require('path');
const fs = require('fs');
const { CozoDb } = require('../index');

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

function tmpFile(suffix) {
  return path.join(os.tmpdir(), `cozo-test-${Date.now()}-${Math.random().toString(36).slice(2)}${suffix}`);
}

// ---------------------------------------------------------------------------
// CozoDb construction & close
// ---------------------------------------------------------------------------

describe('CozoDb construction', () => {
  test('creates an in-memory database', () => {
    const db = new CozoDb('mem', '', {});
    expect(db.dbId).toBeDefined();
    db.close();
  });
});

// ---------------------------------------------------------------------------
// CozoDb.run  (immutable true / false)
// ---------------------------------------------------------------------------

describe('CozoDb.run', () => {
  let db;
  beforeEach(() => { db = new CozoDb('mem', '', {}); });
  afterEach(() => { db.close(); });

  test('run with immutable=false (default) executes a simple query', async () => {
    const res = await db.run('?[x] <- [[1],[2],[3]]', {});
    expect(res.headers).toEqual(['x']);
    expect(res.rows).toEqual([[1], [2], [3]]);
  });

  test('run with immutable=true succeeds for read-only query', async () => {
    const res = await db.run('?[x] <- [[42]]', {}, true);
    expect(res.headers).toEqual(['x']);
    expect(res.rows).toEqual([[42]]);
  });

  test('run with immutable=true rejects a write query', async () => {
    try {
      await db.run(':create tbl {k: Int => v: String}', {}, true);
      expect(true).toBe(false);
    } catch (e) {
      expect(e.ok).toBe(false);
    }
  });

  test('run with params substitutes correctly', async () => {
    const res = await db.run('?[x] <- [[$val]]', { val: 'hello' });
    expect(res.rows).toEqual([['hello']]);
  });

  test('run rejects on invalid CozoScript', async () => {
    try {
      await db.run('THIS IS NOT VALID COZOSCRIPT!!!', {});
      expect(true).toBe(false);
    } catch (e) {
      expect(e.ok).toBe(false);
    }
  });
});

// ---------------------------------------------------------------------------
// CozoDb.multiTransact  (CozoTx)
// ---------------------------------------------------------------------------

describe('multiTransact', () => {
  let db;
  beforeEach(async () => {
    db = new CozoDb('mem', '', {});
    await db.run(':create mt_test {k: Int => v: String}', {});
  });
  afterEach(() => { db.close(); });

  test('write tx: run + commit persists data', async () => {
    const tx = db.multiTransact(true);
    expect(tx.txId).toBeDefined();

    await tx.run('?[k, v] <- [[1, "a"]] :put mt_test {k => v}', {});
    tx.commit();

    const res = await db.run('?[k, v] := *mt_test{k, v}', {});
    expect(res.rows).toEqual([[1, 'a']]);
  });

  test('write tx: run + abort discards data', async () => {
    const tx = db.multiTransact(true);
    await tx.run('?[k, v] <- [[2, "b"]] :put mt_test {k => v}', {});
    tx.abort();

    const res = await db.run('?[k, v] := *mt_test{k, v}', {});
    expect(res.rows.length).toBe(0);
  });

  test('read tx: run succeeds for read-only queries', async () => {
    await db.run('?[k, v] <- [[3, "c"]] :put mt_test {k => v}', {});

    const tx = db.multiTransact(false);
    const res = await tx.run('?[k, v] := *mt_test{k, v}', {});
    expect(res.rows).toEqual([[3, 'c']]);
    tx.commit();
  });
});

// ---------------------------------------------------------------------------
// exportRelations / importRelations
// ---------------------------------------------------------------------------

describe('exportRelations / importRelations', () => {
  test('round-trips data between two databases', async () => {
    const dbA = new CozoDb('mem', '', {});
    const dbB = new CozoDb('mem', '', {});
    try {
      await dbA.run(':create exp_rel {id: Int => name: String}', {});
      await dbA.run('?[id, name] <- [[1,"alpha"],[2,"beta"]] :put exp_rel {id => name}', {});

      // Export returns { rel_name: { headers, rows } }
      const exported = await dbA.exportRelations(['exp_rel']);
      expect(exported).toBeDefined();
      expect(exported.exp_rel).toBeDefined();
      expect(exported.exp_rel.headers).toEqual(['id', 'name']);
      expect(exported.exp_rel.rows.length).toBe(2);

      // Create same schema in dbB, then import
      await dbB.run(':create exp_rel {id: Int => name: String}', {});
      await dbB.importRelations(exported);

      const res = await dbB.run('?[id, name] := *exp_rel{id, name}', {});
      expect(res.rows.length).toBe(2);
      const ids = res.rows.map(r => r[0]).sort();
      expect(ids).toEqual([1, 2]);
    } finally {
      dbA.close();
      dbB.close();
    }
  });
});

// ---------------------------------------------------------------------------
// backup / restore
// ---------------------------------------------------------------------------

describe('backup / restore', () => {
  test('backup and restore round-trips data', async () => {
    const backupPath = tmpFile('.bk');
    const dbA = new CozoDb('mem', '', {});
    const dbB = new CozoDb('mem', '', {});
    try {
      await dbA.run(':create bk_rel {id: Int => val: String}', {});
      await dbA.run('?[id, val] <- [[10,"x"],[20,"y"]] :put bk_rel {id => val}', {});

      await dbA.backup(backupPath);
      expect(fs.existsSync(backupPath)).toBe(true);

      await dbB.restore(backupPath);

      const res = await dbB.run('?[id, val] := *bk_rel{id, val}', {});
      expect(res.rows.length).toBe(2);
    } finally {
      dbA.close();
      dbB.close();
      try { fs.unlinkSync(backupPath); } catch (_) {}
    }
  });
});

// ---------------------------------------------------------------------------
// importRelationsFromBackup
// ---------------------------------------------------------------------------

describe('importRelationsFromBackup', () => {
  test('imports specific relations from a backup file', async () => {
    const backupPath = tmpFile('.bk');
    const dbA = new CozoDb('mem', '', {});
    const dbB = new CozoDb('mem', '', {});
    try {
      await dbA.run(':create ifb_rel {id: Int => val: String}', {});
      await dbA.run('?[id, val] <- [[1,"one"]] :put ifb_rel {id => val}', {});
      await dbA.backup(backupPath);

      await dbB.run(':create ifb_rel {id: Int => val: String}', {});
      await dbB.importRelationsFromBackup(backupPath, ['ifb_rel']);

      const res = await dbB.run('?[id, val] := *ifb_rel{id, val}', {});
      expect(res.rows).toEqual([[1, 'one']]);
    } finally {
      dbA.close();
      dbB.close();
      try { fs.unlinkSync(backupPath); } catch (_) {}
    }
  });
});

// ---------------------------------------------------------------------------
// registerNamedRule / unregisterNamedRule
// ---------------------------------------------------------------------------

describe('registerNamedRule / unregisterNamedRule', () => {
  test('registers, invokes, and unregisters a named rule', async () => {
    const db = new CozoDb('mem', '', {});
    try {
      // inputs is array-of-arrays-of-arrays: [ [[row], [row]], ... ]
      db.registerNamedRule('double_it', 1, async (inputs, _options) => {
        const rows = [];
        for (const binding of inputs) {
          for (const row of binding) {
            rows.push([row[0] * 2]);
          }
        }
        return rows;
      });

      const res = await db.run(
        'input[y] <- [[5],[10]]\n?[x] <~ double_it(input[y])',
        {}
      );
      const vals = res.rows.map(r => r[0]).sort((a, b) => a - b);
      expect(vals).toEqual([10, 20]);

      // Unregister
      db.unregisterNamedRule('double_it');

      // After unregister, invoking should fail
      try {
        await db.run('input[y] <- [[1]]\n?[x] <~ double_it(input[y])', {});
        expect(true).toBe(false);
      } catch (e) {
        expect(e.ok).toBe(false);
      }
    } finally {
      db.close();
    }
  });
});
