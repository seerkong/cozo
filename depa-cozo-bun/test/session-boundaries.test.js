'use strict';
const { test } = require('node:test');
const assert = require('node:assert/strict');
const { CozoDb } = require('../index');

test('transaction isolation, terminal state, query serialization and close rollback', async () => {
  const db = new CozoDb('mem');
  await db.run(':create entries {id => value}');
  const tx = db.multiTransact(true);
  const query = tx.run('?[id, value] <- [[$id, $value]] :put entries {id => value}', { id: 'a', value: '"quoted"\nvalue' });
  assert.throws(() => tx.run('?[x] <- [[1]]'), /pending query/);
  assert.throws(() => tx.commit(), /pending query/);
  assert.throws(() => db.close(), /pending queries/);
  await query;
  tx.abort();
  assert.equal(tx.state, 'aborted');
  assert.throws(() => tx.run('?[x] <- [[1]]'), /aborted/);
  assert.deepEqual((await db.run('?[id] := *entries{id}')).rows, []);
  const next = db.multiTransact(true);
  await next.run('?[id, value] <- [["b", "v"]] :put entries {id => value}');
  db.close();
  assert.equal(next.state, 'aborted');
  db.close();
  assert.throws(() => db.run('?[x] <- [[1]]'), /closed/);
});

test('native errors, immutable guard, recursive timeout and subsequent query', async () => {
  const db = new CozoDb('mem');
  try {
    await assert.rejects(db.run(':create entries {id}', {}, true), error => error instanceof Error && error.ok === false);
    await assert.rejects(db.run('invalid script'), error => error instanceof Error && error.ok === false);
    await assert.rejects(db.run('seed[x] <- [[0]]\nr[x] := seed[x]\nr[y] := r[x], y = x + 1\n?[x] := r[x]\n:timeout 0.001'), error => /timeout|timed out|killed/i.test(error.message));
    assert.deepEqual((await db.run('?[value] <- [[$value]]', { value: 'after timeout' }, true)).rows, [['after timeout']]);
  } finally { db.close(); }
});
