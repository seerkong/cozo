const { CozoDb } = require('../index');
const om = require('../cozo-om');

/**
 * Create a fresh in-memory CozoDB with om schema initialized.
 * Returns { db, om } for convenience.
 */
async function createTestDb() {
  const db = new CozoDb('mem', '', {});
  if (om && typeof om.clearRegistry === 'function') {
    om.clearRegistry();
  }
  await om.initSchema(db);
  return { db, om };
}

/**
 * Create a test DB with a standard type hierarchy pre-seeded:
 *   Resource (root)
 *     ├─ ExecutableResource
 *     │    ├─ ApiService
 *     │    └─ Worker
 *     └─ Dataset
 */
async function createTestDbWithHierarchy() {
  const { db } = await createTestDb();
  await om.defineType(db, 'Resource', 'Resource');
  await om.defineType(db, 'ExecutableResource', 'Executable Resource', { parentType: 'Resource' });
  await om.defineType(db, 'ApiService', 'ApiService', { parentType: 'ExecutableResource' });
  await om.defineType(db, 'Worker', 'Worker', { parentType: 'ExecutableResource' });
  await om.defineType(db, 'Dataset', 'Dataset', { parentType: 'Resource' });
  return { db, om };
}

module.exports = { createTestDb, createTestDbWithHierarchy };
