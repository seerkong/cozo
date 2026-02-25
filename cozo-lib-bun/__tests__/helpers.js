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
 *   Asset (root)
 *     ├─ ITAsset
 *     │    ├─ Server
 *     │    └─ Laptop
 *     └─ Vehicle
 */
async function createTestDbWithHierarchy() {
  const { db } = await createTestDb();
  await om.defineType(db, 'Asset', 'Asset');
  await om.defineType(db, 'ITAsset', 'IT Asset', { parentType: 'Asset' });
  await om.defineType(db, 'Server', 'Server', { parentType: 'ITAsset' });
  await om.defineType(db, 'Laptop', 'Laptop', { parentType: 'ITAsset' });
  await om.defineType(db, 'Vehicle', 'Vehicle', { parentType: 'Asset' });
  return { db, om };
}

module.exports = { createTestDb, createTestDbWithHierarchy };
