# `cozo-lib-bun`

Embedded [CozoDB](https://www.cozodb.org) for Bun runtime.

This package reuses the native Node-API addon from `cozo-node` and loads it directly in Bun.

## Build native addon

From the repository root:

```bash
cargo build --release -p cozo-node -F compact -F storage-rocksdb
mkdir -p cozo-lib-bun/native
cp target/release/libcozo_node.dylib cozo-lib-bun/native/cozo_bun.node
```

On Linux/Windows, replace `libcozo_node.dylib` with the corresponding shared library output.

## Usage

```javascript
const { CozoDb, om, dsl } = require('cozo-lib-bun');

const db = new CozoDb();

async function main() {
  const r1 = await db.run("?[] <- [['hello', 'bun']] ");
  const r2 = await db.run('?[] <- [[1, 2, $x]]', { x: 3 });
  await om.initSchema(db);
  await om.defineType(db, 'User', 'User object');
  await om.defineAttribute(db, 'User', 'email', 'String', true);
  await om.createEntity(db, 'u:alice', 'User', 'Alice');
  await om.setProperty(db, 'u:alice', 'email', 'alice@example.com');
  const user = await om.getEntityView(db, 'u:alice');

  const q = dsl
    .query()
    .select(['type_name'])
    .fromStored('om_entity', { id: dsl.param('id', 'u:alice'), type_name: dsl.var('type_name'), label: dsl.var('_label') })
    .limit(1)
    .build();

  const typedRows = await db.run(q.script, q.params);
  console.log(r1, r2, user, typedRows.rows);
  db.close();
}

main().catch((e) => {
  console.error(e.display || e.message || e);
});
```

## Local test

```bash
bun example.js
```

## OOP modeling prototype

This package includes an independent OOP modeling library `cozo-om.js` and a runnable demo `oop-prototype.js`.

Core model:

- `om_entity`: object instances (`id => type_name, label`)
- `om_property`: dynamic properties (`entity_id, attr_name => value`)
- `om_edge`: typed relations between objects (`from_id, rel_name, to_id => props`)
- metadata relations: `om_type`, `om_attr_def`, `om_rel_def`

Main APIs in `om`:

- schema/type modeling: `initSchema`, `defineType`, `defineAttribute`, `defineRelation`
- data writing with validation: `createEntity`, `setProperty`, `linkEntities`, `ingestBatch`
- validation: `validateEntity`, `finalizeEntity`
- analysis: `traverse`, `getNeighbors`, `findByType`, `aggregateByType`

Language-internal query DSL (`dsl`) APIs:

- builder: `dsl.query()`
- safe tokens: `dsl.var(name)` (variable), `dsl.param(name, value)` (named parameter, `dsl.p` is alias)
- conditions (v2): `dsl.and(...)`, `dsl.or(...)`, `dsl.not(...)`, `dsl.eq(...)`, `dsl.neq(...)`, `dsl.gt(...)`, `dsl.lt(...)`, `dsl.gte(...)`, `dsl.lte(...)`, `dsl.in(...)`
- building output: `.build()` -> `{ script, params }`
- execution: `db.run(script, params)` or `builder.execute(runner)`

Reusable analysis templates (`om`) APIs:

- `om.impactAnalysis(runner, { rootId, relNames?, maxDepth?, direction? })`
- `om.ownershipTree(runner, { rootId, ownerRelNames?, maxDepth? })`
- `om.riskHotspot(runner, { typeName?, riskAttr?, topK?, minScore?, degreeWeight? })`

Run it with:

```bash
bun oop-prototype.js
# or
bun run example:oop
```

The script demonstrates:

- defining object types and attribute definitions,
- creating entities and dynamic properties,
- creating typed relations (edges),
- graph-style traversal queries (owner -> project -> task),
- filtering and neighborhood analysis,
- aggregation (e.g. task effort sum/avg),
- DSL v2 condition composition (`and/or/not/in/eq/neq/gt/lt/gte/lte`),
- reusable analysis templates (`impactAnalysis`, `ownershipTree`, `riskHotspot`) with visualization-friendly `data.visual` outputs,
- building an entity view combining base fields, properties, and outgoing relations.

Constraint checks are included in the prototype now:

- property type validation,
- relation endpoint type validation,
- required property validation via explicit finalize step.

Design notes are available at:

- `constraint-design.md`
- `ontology-modeling-design.md`
- `dsl-design.md`
