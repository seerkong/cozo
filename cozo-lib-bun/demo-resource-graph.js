const { CozoDb, om } = require('.');

async function main() {
  const db = new CozoDb();
  try {
    await om.initSchema(db);

    await om.defineType(db, 'Resource', 'Generic resource');
    await om.defineType(db, 'ExecutableResource', 'Executable resource', { parentType: 'Resource' });
    await om.defineType(db, 'ApiService', 'API service', { parentType: 'ExecutableResource' });
    await om.defineType(db, 'Dataset', 'Dataset', { parentType: 'Resource' });

    await om.defineMixin(db, 'Auditable', 'Tracks audit metadata');
    await om.defineAttribute(db, 'Auditable', 'last_reviewed', 'String', false, 'Last review time');
    await om.defineAttribute(db, 'Resource', 'resource_key', 'String', true, 'Resource identifier');
    await om.defineAttribute(db, 'Resource', 'scope', 'String', false, 'Owning scope');
    await om.defineAttribute(db, 'ApiService', 'endpoint_count', 'Number', false, 'Published endpoints');
    await om.defineAttribute(db, 'Dataset', 'size_mb', 'Number', false, 'Dataset size in MB');

    await om.defineRelation(db, 'depends_on', 'ExecutableResource', 'ExecutableResource', true, 'Dependency relation');
    await om.defineRelation(db, 'produces', 'ExecutableResource', 'Dataset', true, 'Produces data');

    await om.createEntity(db, 'api:gateway', 'ApiService', 'Gateway API');
    await om.setProperty(db, 'api:gateway', 'resource_key', 'gateway-api');
    await om.setProperty(db, 'api:gateway', 'endpoint_count', 18);
    await om.createEntity(db, 'api:catalog', 'ApiService', 'Catalog API');
    await om.setProperty(db, 'api:catalog', 'resource_key', 'catalog-api');
    await om.setProperty(db, 'api:catalog', 'endpoint_count', 11);
    await om.createEntity(db, 'data:catalog', 'Dataset', 'Catalog Dataset');
    await om.setProperty(db, 'data:catalog', 'resource_key', 'catalog-data');
    await om.setProperty(db, 'data:catalog', 'size_mb', 640);

    await om.linkEntities(db, 'api:gateway', 'depends_on', 'api:catalog');
    await om.linkEntities(db, 'api:catalog', 'produces', 'data:catalog');

    console.log('=== Generic Resource Graph ===\n');
    console.log('Ancestors:', await om.getAncestors(db, 'ApiService'));
    console.log('Descendants:', await om.getDescendants(db, 'Resource'));
    console.log('Polymorphic resources:', await om.findByType(db, 'Resource'));
    console.log('Dependency impact:', (await om.impactAnalysis(db, {
      rootId: 'api:gateway',
      relNames: ['depends_on', 'produces'],
      maxDepth: 3,
      direction: 'outgoing',
    })).stats);
  } finally {
    db.close();
  }
}

main().catch((error) => {
  console.error(error.display || error.message || error);
  process.exitCode = 1;
});
