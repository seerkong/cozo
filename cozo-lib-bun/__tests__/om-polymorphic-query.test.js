const { expect, test, beforeAll, afterAll } = require('bun:test');

const { CozoDb } = require('../index');
const om = require('../cozo-om');

let db;

beforeAll(async () => {
  db = new CozoDb('mem', '', {});
  await om.initSchema(db);

  // Define type hierarchy: Resource -> ExecutableResource -> ApiService
  await om.defineType(db, 'Resource', 'Base resource');
  await om.defineType(db, 'ExecutableResource', 'Executable resource', { parentType: 'Resource' });
  await om.defineType(db, 'ApiService', 'Physical apiService', { parentType: 'ExecutableResource' });

  // Define attribute on root type
  await om.defineAttribute(db, 'Resource', 'value', 'Number', false);

  // 2 Resource entities
  await om.createEntity(db, 'a:1', 'Resource', 'Resource One');
  await om.setProperty(db, 'a:1', 'value', 100);
  await om.createEntity(db, 'a:2', 'Resource', 'Resource Two');
  await om.setProperty(db, 'a:2', 'value', 200);

  // 3 ExecutableResource entities
  await om.createEntity(db, 'exec:1', 'ExecutableResource', 'Executable Resource One');
  await om.setProperty(db, 'exec:1', 'value', 300);
  await om.createEntity(db, 'exec:2', 'ExecutableResource', 'Executable Resource Two');
  await om.setProperty(db, 'exec:2', 'value', 400);
  await om.createEntity(db, 'exec:3', 'ExecutableResource', 'Executable Resource Three');
  await om.setProperty(db, 'exec:3', 'value', 500);

  // 1 ApiService entity
  await om.createEntity(db, 'srv:1', 'ApiService', 'ApiService One');
  await om.setProperty(db, 'srv:1', 'value', 600);
});

afterAll(() => {
  db.close();
});

test('findByType Resource (polymorphic) returns 6 entries', async () => {
  const results = await om.findByType(db, 'Resource');
  expect(results.length).toBe(6);
});

test('findByType Resource exact returns 2 entries', async () => {
  const results = await om.findByType(db, 'Resource', {}, { exact: true });
  expect(results.length).toBe(2);
});

test('findByType supports property equality filters and returns properties', async () => {
  const results = await om.findByType(db, 'Resource', { value: 600 });
  expect(results.length).toBe(1);
  expect(results[0].id).toBe('srv:1');
  expect(results[0].properties.value).toBe(600);
});

test('findByType property filters respect exact mode', async () => {
  const polymorphic = await om.findByType(db, 'Resource', { value: 600 });
  const exact = await om.findByType(db, 'Resource', { value: 600 }, { exact: true });
  expect(polymorphic.length).toBe(1);
  expect(exact.length).toBe(0);
});

test('aggregateByType Resource value sum returns sum over 6', async () => {
  const total = await om.aggregateByType(db, 'Resource', 'value', 'sum');
  // 100 + 200 + 300 + 400 + 500 + 600 = 2100
  expect(total).toBe(2100);
});

test('aggregateByType supports avg/min/max/count over descendants and exact mode', async () => {
  await expect(om.aggregateByType(db, 'Resource', 'value', 'avg')).resolves.toBe(350);
  await expect(om.aggregateByType(db, 'Resource', 'value', 'min')).resolves.toBe(100);
  await expect(om.aggregateByType(db, 'Resource', 'value', 'max')).resolves.toBe(600);
  await expect(om.aggregateByType(db, 'Resource', 'value', 'count')).resolves.toBe(6);
  await expect(om.aggregateByType(db, 'Resource', 'value', 'sum', { exact: true })).resolves.toBe(300);
  await expect(om.aggregateByType(db, 'Resource', 'value', 'count', { exact: true })).resolves.toBe(2);
});
