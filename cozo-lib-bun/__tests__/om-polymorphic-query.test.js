const { expect, test, beforeAll, afterAll } = require('bun:test');

const { CozoDb } = require('../index');
const om = require('../cozo-om');

let db;

beforeAll(async () => {
  db = new CozoDb('mem', '', {});
  await om.initSchema(db);

  // Define type hierarchy: Asset -> ITAsset -> Server
  await om.defineType(db, 'Asset', 'Base asset');
  await om.defineType(db, 'ITAsset', 'IT asset', { parentType: 'Asset' });
  await om.defineType(db, 'Server', 'Physical server', { parentType: 'ITAsset' });

  // Define attribute on root type
  await om.defineAttribute(db, 'Asset', 'value', 'Number', false);

  // 2 Asset entities
  await om.createEntity(db, 'a:1', 'Asset', 'Asset One');
  await om.setProperty(db, 'a:1', 'value', 100);
  await om.createEntity(db, 'a:2', 'Asset', 'Asset Two');
  await om.setProperty(db, 'a:2', 'value', 200);

  // 3 ITAsset entities
  await om.createEntity(db, 'it:1', 'ITAsset', 'IT Asset One');
  await om.setProperty(db, 'it:1', 'value', 300);
  await om.createEntity(db, 'it:2', 'ITAsset', 'IT Asset Two');
  await om.setProperty(db, 'it:2', 'value', 400);
  await om.createEntity(db, 'it:3', 'ITAsset', 'IT Asset Three');
  await om.setProperty(db, 'it:3', 'value', 500);

  // 1 Server entity
  await om.createEntity(db, 'srv:1', 'Server', 'Server One');
  await om.setProperty(db, 'srv:1', 'value', 600);
});

afterAll(() => {
  db.close();
});

test('findByType Asset (polymorphic) returns 6 entries', async () => {
  const results = await om.findByType(db, 'Asset');
  expect(results.length).toBe(6);
});

test('findByType Asset exact returns 2 entries', async () => {
  const results = await om.findByType(db, 'Asset', {}, { exact: true });
  expect(results.length).toBe(2);
});

test('findByType supports property equality filters and returns properties', async () => {
  const results = await om.findByType(db, 'Asset', { value: 600 });
  expect(results.length).toBe(1);
  expect(results[0].id).toBe('srv:1');
  expect(results[0].properties.value).toBe(600);
});

test('findByType property filters respect exact mode', async () => {
  const polymorphic = await om.findByType(db, 'Asset', { value: 600 });
  const exact = await om.findByType(db, 'Asset', { value: 600 }, { exact: true });
  expect(polymorphic.length).toBe(1);
  expect(exact.length).toBe(0);
});

test('aggregateByType Asset value sum returns sum over 6', async () => {
  const total = await om.aggregateByType(db, 'Asset', 'value', 'sum');
  // 100 + 200 + 300 + 400 + 500 + 600 = 2100
  expect(total).toBe(2100);
});

test('aggregateByType supports avg/min/max/count over descendants and exact mode', async () => {
  await expect(om.aggregateByType(db, 'Asset', 'value', 'avg')).resolves.toBe(350);
  await expect(om.aggregateByType(db, 'Asset', 'value', 'min')).resolves.toBe(100);
  await expect(om.aggregateByType(db, 'Asset', 'value', 'max')).resolves.toBe(600);
  await expect(om.aggregateByType(db, 'Asset', 'value', 'count')).resolves.toBe(6);
  await expect(om.aggregateByType(db, 'Asset', 'value', 'sum', { exact: true })).resolves.toBe(300);
  await expect(om.aggregateByType(db, 'Asset', 'value', 'count', { exact: true })).resolves.toBe(2);
});
