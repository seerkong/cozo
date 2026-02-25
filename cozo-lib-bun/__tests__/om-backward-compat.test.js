const { expect, test } = require('bun:test');

const { CozoDb } = require('../index');
const om = require('../cozo-om');

test('backward compatibility: defineType(name, description) still works', async () => {
  const db = new CozoDb('mem', '', {});
  try {
    await om.initSchema(db);
    await om.defineType(db, 'Supplier', 'Supplier');
    await om.defineAttribute(db, 'Supplier', 'name', 'String', true);
    await om.defineRelation(db, 'supplies', 'Supplier', 'Supplier', true);

    await om.createEntity(db, 'sup:1', 'Supplier', 'ACME');
    await om.setProperty(db, 'sup:1', 'name', 'ACME');
    await om.finalizeEntity(db, 'sup:1');

    await om.createEntity(db, 'sup:2', 'Supplier', 'FOO');
    await om.setProperty(db, 'sup:2', 'name', 'FOO');
    await om.linkEntities(db, 'sup:1', 'supplies', 'sup:2', { since: '2026-01-01' });

    const view = await om.getEntityView(db, 'sup:1');
    expect(view).toBeTruthy();
    expect(view.typeName).toBe('Supplier');
    expect(view.properties.name).toBe('ACME');
    expect(Array.isArray(view.outgoing)).toBe(true);
  } finally {
    db.close();
  }
});

test('entity writes reject unknown type names', async () => {
  const db = new CozoDb('mem', '', {});
  try {
    await om.initSchema(db);

    await expect(om.createEntity(db, 'missing:create', 'MissingType', 'Missing')).rejects.toThrow(
      "Type 'MissingType' does not exist"
    );
    await expect(om.upsertEntity(db, 'missing:upsert', 'MissingType', 'Missing')).rejects.toThrow(
      "Type 'MissingType' does not exist"
    );
  } finally {
    db.close();
  }
});
