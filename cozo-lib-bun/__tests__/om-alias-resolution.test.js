const { describe, test, expect } = require('bun:test');

const dsl = require('../cozo-dsl');
const { createTestDb } = require('./helpers');

async function putAliasAttr(db, typeName, aliasAttr, canonicalAttr) {
  const built = dsl
    .query()
    .input({
      type_name: dsl.param('type_name', typeName),
      alias_attr: dsl.param('alias_attr', aliasAttr),
      canonical_attr: dsl.param('canonical_attr', canonicalAttr),
    })
    .put('om_alias_attr', ['type_name', 'alias_attr'], ['canonical_attr'])
    .build();
  await db.run(built.script, built.params);
}

async function putAliasType(db, alias, canonical) {
  const built = dsl
    .query()
    .input({
      alias: dsl.param('alias', alias),
      canonical: dsl.param('canonical', canonical),
    })
    .put('om_alias_type', ['alias'], ['canonical'])
    .build();
  await db.run(built.script, built.params);
}

async function putAliasRel(db, alias, canonical) {
  const built = dsl
    .query()
    .input({
      alias: dsl.param('alias', alias),
      canonical: dsl.param('canonical', canonical),
    })
    .put('om_alias_rel', ['alias'], ['canonical'])
    .build();
  await db.run(built.script, built.params);
}

async function putRawProperty(db, entityId, attrName, value, validTime) {
  const built = dsl
    .query()
    .input({
      entity_id: dsl.param('entity_id', entityId),
      attr_name: dsl.param('attr_name', attrName),
      valid_time: dsl.param('valid_time', validTime || '2000-01-01T00:00:00Z'),
      value: dsl.param('value', value),
      tx_time: dsl.param('tx_time', '2000-01-01T00:00:00Z'),
    })
    .put('om_property', ['entity_id', 'attr_name', 'valid_time'], ['value', 'tx_time'])
    .build();
  await db.run(built.script, built.params);
}

describe('P1/WAVE-P1-02 (T1.2.1): alias resolution', () => {
  test('alias resolution for attributes (om_alias_attr)', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Employee', 'Employee');
      await om.defineAttribute(db, 'Employee', 'org_unit', 'String', true);
      await putAliasAttr(db, 'Employee', 'department', 'org_unit');

      await om.createEntity(db, 'emp:alias-1', 'Employee', 'Alice');
      await om.setProperty(db, 'emp:alias-1', 'department', 'Engineering', {
        validTime: '2000-01-01T00:00:00Z',
      });

      await expect(om.getProperty(db, 'emp:alias-1', 'org_unit')).resolves.toBe('Engineering');
      await expect(om.getProperty(db, 'emp:alias-1', 'department')).resolves.toBe('Engineering');
    } finally {
      db.close();
    }
  });

  test('canonical wins when both canonical and alias values exist', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Employee', 'Employee');
      await om.defineAttribute(db, 'Employee', 'org_unit', 'String', true);
      await putAliasAttr(db, 'Employee', 'department', 'org_unit');

      await om.createEntity(db, 'emp:alias-2', 'Employee', 'Bob');

      await om.setProperty(db, 'emp:alias-2', 'org_unit', 'CanonicalValue', {
        validTime: '2000-01-01T00:00:00Z',
      });

      // Ensure the alias-named attribute entry exists independently in storage.
      await putRawProperty(db, 'emp:alias-2', 'department', 'AliasValue', '2000-01-01T00:00:00Z');

      await expect(om.getProperty(db, 'emp:alias-2', 'org_unit')).resolves.toBe('CanonicalValue');
    } finally {
      db.close();
    }
  });

  test('alias fallback when canonical missing', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Employee', 'Employee');
      await om.defineAttribute(db, 'Employee', 'org_unit', 'String', true);
      await putAliasAttr(db, 'Employee', 'department', 'org_unit');

      await om.createEntity(db, 'emp:alias-3', 'Employee', 'Carol');

      // Only store under the alias name.
      await putRawProperty(db, 'emp:alias-3', 'department', 'Support', '2000-01-01T00:00:00Z');

      await expect(om.getProperty(db, 'emp:alias-3', 'org_unit')).resolves.toBe('Support');
    } finally {
      db.close();
    }
  });

  test("type alias impacts entity type lookups (getEntityView returns canonical typeName)", async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Employee', 'Employee');
      await om.defineAttribute(db, 'Employee', 'org_unit', 'String', true);

      await putAliasType(db, 'Staff', 'Employee');

      // Store the alias type name in om_entity to ensure lookups resolve it.
      const built = dsl
        .query()
        .input({
          id: dsl.param('id', 'emp:alias-4'),
          type_name: dsl.param('type_name', 'Staff'),
          label: dsl.param('label', 'Dora'),
        })
        .insert('om_entity', ['id'], ['type_name', 'label'])
        .build();
      await db.run(built.script, built.params);

      await om.setProperty(db, 'emp:alias-4', 'org_unit', 'PeopleOps', {
        validTime: '2000-01-01T00:00:00Z',
      });

      const view = await om.getEntityView(db, 'emp:alias-4');
      expect(view).toBeTruthy();
      expect(view.typeName).toBe('Employee');
      expect(view.properties.org_unit).toBe('PeopleOps');
    } finally {
      db.close();
    }
  });

  test('cycle detection in resolveType', async () => {
    const { db, om } = await createTestDb();
    try {
      await putAliasType(db, 'A', 'B');
      await putAliasType(db, 'B', 'A');

      await expect(om.resolveType(db, 'A')).rejects.toThrow(/cycle/i);
    } finally {
      db.close();
    }
  });

  test('relation alias resolves for links and explicit resolveRel', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Employee', 'Employee');
      await om.defineType(db, 'Department', 'Department');
      await om.defineRelation(db, 'works_in', 'Employee', 'Department', true);
      await putAliasRel(db, 'member_of', 'works_in');

      expect(await om.resolveRel(db, 'member_of')).toBe('works_in');

      await om.createEntity(db, 'emp:alias-rel', 'Employee', 'Alice');
      await om.createEntity(db, 'dept:alias-rel', 'Department', 'Engineering');
      await om.linkEntities(db, 'emp:alias-rel', 'member_of', 'dept:alias-rel');

      const neighbors = await om.getNeighbors(db, 'emp:alias-rel', 'works_in');
      expect(neighbors.outgoing.map((n) => n.entityId)).toContain('dept:alias-rel');
    } finally {
      db.close();
    }
  });

  test('cycle detection in resolveAttr', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Employee', 'Employee');
      await putAliasAttr(db, 'Employee', 'a', 'b');
      await putAliasAttr(db, 'Employee', 'b', 'a');

      await expect(om.resolveAttr(db, 'Employee', 'a')).rejects.toThrow(/cycle/i);
    } finally {
      db.close();
    }
  });
});
