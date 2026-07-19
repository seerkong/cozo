const { expect, test, describe } = require('bun:test');
const { createTestDb, createTestDbWithHierarchy } = require('./helpers');
const dsl = require('../cozo-dsl');

describe('Phase 8: description support', () => {
  test('initSchema creates om_attr_desc and om_rel_desc relations', async () => {
    const { db } = await createTestDb();
    try {
      const q1 = dsl.query()
        .select(['type_name'])
        .fromStored('om_attr_desc', {
          type_name: dsl.var('type_name'),
          attr_name: dsl.var('_a'),
          description: dsl.var('_d'),
        })
        .limit(1)
        .build();
      await expect(db.run(q1.script, q1.params)).resolves.toBeTruthy();

      const q2 = dsl.query()
        .select(['rel_name'])
        .fromStored('om_rel_desc', {
          rel_name: dsl.var('rel_name'),
          description: dsl.var('_d'),
        })
        .limit(1)
        .build();
      await expect(db.run(q2.script, q2.params)).resolves.toBeTruthy();
    } finally {
      db.close();
    }
  });

  test('defineAttribute with description stores it', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Resource', 'Resource');
      await om.defineAttribute(db, 'Resource', 'name', 'String', true, 'The resource name');

      const defs = await om.getAttributeDefinitions(db, 'Resource');
      const nameDef = defs.get('name');
      expect(nameDef).toBeTruthy();
      expect(nameDef.description).toBe('The resource name');
    } finally {
      db.close();
    }
  });

  test('defineAttribute without description does not set description', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Resource', 'Resource');
      await om.defineAttribute(db, 'Resource', 'name', 'String', false);

      const defs = await om.getAttributeDefinitions(db, 'Resource');
      const nameDef = defs.get('name');
      expect(nameDef).toBeTruthy();
      expect(nameDef.description).toBeUndefined();
    } finally {
      db.close();
    }
  });

  test('description inherits with correct precedence', async () => {
    const { db, om } = await createTestDbWithHierarchy();
    try {
      await om.defineAttribute(db, 'Resource', 'name', 'String', false, 'Base name');
      await om.defineAttribute(db, 'ExecutableResource', 'name', 'String', false, 'IT name');

      const resourceDefs = await om.getAttributeDefinitions(db, 'Resource');
      expect(resourceDefs.get('name').description).toBe('Base name');

      const itDefs = await om.getAttributeDefinitions(db, 'ExecutableResource');
      expect(itDefs.get('name').description).toBe('IT name');

      // ApiService inherits from ExecutableResource (nearest ancestor wins)
      const apiServiceDefs = await om.getAttributeDefinitions(db, 'ApiService');
      expect(apiServiceDefs.get('name').description).toBe('IT name');
    } finally {
      db.close();
    }
  });

  test('defineRelation with description stores it in om_rel_desc', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Resource', 'Resource');
      await om.defineRelation(db, 'depends_on', 'Resource', 'Resource', true, 'Dependency link');

      const q = dsl.query()
        .select(['description'])
        .fromStored('om_rel_desc', {
          rel_name: dsl.param('rel_name', 'depends_on'),
          description: dsl.var('description'),
        })
        .limit(1)
        .build();
      const result = await db.run(q.script, q.params);
      expect(result.rows.length).toBe(1);
      expect(result.rows[0][0]).toBe('Dependency link');
    } finally {
      db.close();
    }
  });

  test('defineRelation without description does not write to om_rel_desc', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Resource', 'Resource');
      await om.defineRelation(db, 'depends_on', 'Resource', 'Resource', true);

      const q = dsl.query()
        .select(['description'])
        .fromStored('om_rel_desc', {
          rel_name: dsl.param('rel_name', 'depends_on'),
          description: dsl.var('description'),
        })
        .limit(1)
        .build();
      const result = await db.run(q.script, q.params);
      expect(result.rows.length).toBe(0);
    } finally {
      db.close();
    }
  });
});
