const { expect, test } = require('bun:test');

const dsl = require('../cozo-dsl');
const { CozoDb } = require('../index');
const om = require('../cozo-om');

test('initSchema creates om_type with parent_type column (and stores parentType)', async () => {
  const db = new CozoDb('mem', '', {});
  try {
    await om.initSchema(db);

    await om.defineType(db, 'Resource', 'Resource');
    await om.defineType(db, 'ExecutableResource', 'Executable Resource', { parentType: 'Resource' });

    const built = dsl.query()
        .select(['parent_type'])
        .fromStored('om_type', {
          name: dsl.param('name', 'ExecutableResource'),
          description: dsl.var('_desc'),
          parent_type: dsl.var('parent_type'),
        })
        .limit(1)
        .build();
    const result = await db.run(built.script, built.params);
    expect(Array.isArray(result.rows)).toBe(true);
    expect(result.rows.length).toBe(1);
    expect(result.rows[0][0]).toBe('Resource');
  } finally {
    db.close();
  }
});

test('initSchema creates mixin relations om_mixin and om_type_mixin', async () => {
  const db = new CozoDb('mem', '', {});
  try {
    await om.initSchema(db);

    const q1 = dsl.query()
      .select(['name'])
      .fromStored('om_mixin', { name: dsl.var('name'), description: dsl.var('_d') })
      .limit(1)
      .build();
    await expect(db.run(q1.script, q1.params)).resolves.toBeTruthy();

    const q2 = dsl.query()
      .select(['type_name'])
      .fromStored('om_type_mixin', { type_name: dsl.var('type_name'), mixin_name: dsl.var('_m') })
      .limit(1)
      .build();
    await expect(db.run(q2.script, q2.params)).resolves.toBeTruthy();
  } finally {
    db.close();
  }
});
