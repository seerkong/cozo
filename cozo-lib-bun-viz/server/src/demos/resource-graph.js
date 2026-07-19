/**
 * Generic resource graph demo -- inheritance, mixins, polymorphic queries, and graph analysis.
 */
const { dsl, om } = require('cozo-lib-bun');

async function defineOntology(db) {
  await om.initSchema(db);
  await om.defineMixin(db, 'Auditable', '\u6df7\u5165: \u5ba1\u8ba1\u8ddf\u8e2a\u5b57\u6bb5');

  await om.defineType(db, 'Resource', '\u53ef\u7ec4\u7ec7\u7684\u8d44\u6e90\u6839\u7c7b\u578b', { mixins: ['Auditable'] });
  await om.defineType(db, 'ExecutableResource', '\u53ef\u6267\u884c\u8d44\u6e90', { parentType: 'Resource', mixins: ['Auditable'] });
  await om.defineType(db, 'ApiService', 'API \u670d\u52a1', { parentType: 'ExecutableResource' });
  await om.defineType(db, 'Worker', '\u540e\u53f0\u4efb\u52a1', { parentType: 'ExecutableResource' });
  await om.defineType(db, 'Dataset', '\u6570\u636e\u96c6', { parentType: 'Resource' });

  await om.defineAttribute(db, 'Resource', 'resource_key', 'String', true, '\u8d44\u6e90\u6807\u8bc6');
  await om.defineAttribute(db, 'Resource', 'state', 'String', true, '\u5f53\u524d\u72b6\u6001');
  await om.defineAttribute(db, 'Resource', 'scope', 'String', false, '\u6240\u5c5e\u8303\u56f4');
  await om.defineAttribute(db, 'ExecutableResource', 'runtime', 'String', false, '\u8fd0\u884c\u73af\u5883');
  await om.defineAttribute(db, 'ApiService', 'endpoint_count', 'Number', true, '\u7aef\u70b9\u6570');
  await om.defineAttribute(db, 'Worker', 'throughput', 'Number', true, '\u5408\u5e76\u540e\u5904\u7406\u91cf');
  await om.defineAttribute(db, 'Dataset', 'schema_version', 'String', true, '\u6a21\u5f0f\u7248\u672c');
  await om.defineAttribute(db, 'Dataset', 'size_mb', 'Number', false, '\u5927\u5c0f (MB)');
  await om.defineAttribute(db, 'Auditable', 'created_by', 'String', true, '\u521b\u5efa\u8005');
  await om.defineAttribute(db, 'Auditable', 'updated_at', 'String', true, '\u66f4\u65b0\u65f6\u95f4');

  await om.defineRelation(db, 'contained_in', 'Resource', 'Resource', true, '\u5305\u542b\u4e8e');
  await om.defineRelation(db, 'depends_on', 'ExecutableResource', 'ExecutableResource', true, '\u4f9d\u8d56\u4e8e');
  await om.defineRelation(db, 'produces', 'ExecutableResource', 'Dataset', true, '\u4ea7\u751f');
}

const defaultSheets = {
  entities: {
    columns: ['id', 'typeName', 'label'],
    rows: [
      { id: 'api:gateway', typeName: 'ApiService', label: 'Gateway API' },
      { id: 'api:catalog', typeName: 'ApiService', label: 'Catalog API' },
      { id: 'worker:indexer', typeName: 'Worker', label: 'Index Builder' },
      { id: 'data:catalog', typeName: 'Dataset', label: 'Catalog Dataset' },
      { id: 'data:index', typeName: 'Dataset', label: 'Search Index' },
      { id: 'scope:platform', typeName: 'Resource', label: 'Platform Scope' },
    ],
  },
  properties: {
    columns: ['entityId', 'attrName', 'value'],
    rows: [
      { entityId: 'api:gateway', attrName: 'resource_key', value: 'gateway-api' },
      { entityId: 'api:gateway', attrName: 'state', value: 'active' },
      { entityId: 'api:gateway', attrName: 'runtime', value: 'node' },
      { entityId: 'api:gateway', attrName: 'endpoint_count', value: 18 },
      { entityId: 'api:gateway', attrName: 'created_by', value: 'platform-team' },
      { entityId: 'api:gateway', attrName: 'updated_at', value: '2026-02-01' },
      { entityId: 'api:catalog', attrName: 'resource_key', value: 'catalog-api' },
      { entityId: 'api:catalog', attrName: 'state', value: 'active' },
      { entityId: 'api:catalog', attrName: 'runtime', value: 'java' },
      { entityId: 'api:catalog', attrName: 'endpoint_count', value: 11 },
      { entityId: 'api:catalog', attrName: 'created_by', value: 'platform-team' },
      { entityId: 'api:catalog', attrName: 'updated_at', value: '2026-01-20' },
      { entityId: 'worker:indexer', attrName: 'resource_key', value: 'index-builder' },
      { entityId: 'worker:indexer', attrName: 'state', value: 'maintenance' },
      { entityId: 'worker:indexer', attrName: 'runtime', value: 'python' },
      { entityId: 'worker:indexer', attrName: 'throughput', value: 1200 },
      { entityId: 'worker:indexer', attrName: 'created_by', value: 'data-team' },
      { entityId: 'worker:indexer', attrName: 'updated_at', value: '2026-02-15' },
      { entityId: 'data:catalog', attrName: 'resource_key', value: 'catalog-data' },
      { entityId: 'data:catalog', attrName: 'state', value: 'active' },
      { entityId: 'data:catalog', attrName: 'schema_version', value: 'v3' },
      { entityId: 'data:catalog', attrName: 'size_mb', value: 640 },
      { entityId: 'data:catalog', attrName: 'created_by', value: 'data-team' },
      { entityId: 'data:catalog', attrName: 'updated_at', value: '2026-01-05' },
      { entityId: 'data:index', attrName: 'resource_key', value: 'search-index' },
      { entityId: 'data:index', attrName: 'state', value: 'active' },
      { entityId: 'data:index', attrName: 'schema_version', value: 'v7' },
      { entityId: 'data:index', attrName: 'size_mb', value: 380 },
      { entityId: 'data:index', attrName: 'created_by', value: 'data-team' },
      { entityId: 'data:index', attrName: 'updated_at', value: '2026-02-10' },
      { entityId: 'scope:platform', attrName: 'resource_key', value: 'platform' },
      { entityId: 'scope:platform', attrName: 'state', value: 'active' },
      { entityId: 'scope:platform', attrName: 'created_by', value: 'platform-team' },
      { entityId: 'scope:platform', attrName: 'updated_at', value: '2025-12-01' },
    ],
  },
  edges: {
    columns: ['fromId', 'relName', 'toId', 'props'],
    rows: [
      { fromId: 'api:gateway', relName: 'contained_in', toId: 'scope:platform', props: {} },
      { fromId: 'api:catalog', relName: 'contained_in', toId: 'scope:platform', props: {} },
      { fromId: 'worker:indexer', relName: 'contained_in', toId: 'scope:platform', props: {} },
      { fromId: 'data:catalog', relName: 'contained_in', toId: 'scope:platform', props: {} },
      { fromId: 'data:index', relName: 'contained_in', toId: 'scope:platform', props: {} },
      { fromId: 'api:gateway', relName: 'depends_on', toId: 'api:catalog', props: { protocol: 'http' } },
      { fromId: 'api:catalog', relName: 'produces', toId: 'data:catalog', props: {} },
      { fromId: 'worker:indexer', relName: 'produces', toId: 'data:index', props: {} },
    ],
  },
};

const defaultTables = [
  {
    name: '\u7c7b\u578b\u5b9a\u4e49',
    columns: ['typeName', 'parent_type', 'mixins', 'description'],
    rows: [
      { typeName: 'Resource', parent_type: '', mixins: 'Auditable', description: '\u8d44\u6e90\u6839\u7c7b\u578b' },
      { typeName: 'ExecutableResource', parent_type: 'Resource', mixins: 'Auditable', description: '\u53ef\u6267\u884c\u8d44\u6e90' },
      { typeName: 'ApiService', parent_type: 'ExecutableResource', mixins: '', description: 'API \u670d\u52a1' },
      { typeName: 'Worker', parent_type: 'ExecutableResource', mixins: '', description: '\u540e\u53f0\u4efb\u52a1' },
      { typeName: 'Dataset', parent_type: 'Resource', mixins: '', description: '\u6570\u636e\u96c6' },
      { typeName: 'Auditable', parent_type: '', mixins: '', description: '\u6df7\u5165: \u5ba1\u8ba1\u8ddf\u8e2a\u5b57\u6bb5' },
    ],
  },
  {
    name: '\u5c5e\u6027\u5b9a\u4e49',
    columns: ['typeName', 'attrName', 'valueType', 'required', 'description'],
    rows: [
      { typeName: 'Resource', attrName: 'resource_key', valueType: 'String', required: true, description: '\u8d44\u6e90\u6807\u8bc6' },
      { typeName: 'Resource', attrName: 'state', valueType: 'String', required: true, description: '\u5f53\u524d\u72b6\u6001' },
      { typeName: 'Resource', attrName: 'scope', valueType: 'String', required: false, description: '\u6240\u5c5e\u8303\u56f4' },
      { typeName: 'ExecutableResource', attrName: 'runtime', valueType: 'String', required: false, description: '\u8fd0\u884c\u73af\u5883' },
      { typeName: 'ApiService', attrName: 'endpoint_count', valueType: 'Number', required: true, description: '\u7aef\u70b9\u6570' },
      { typeName: 'Worker', attrName: 'throughput', valueType: 'Number', required: true, description: '\u5408\u5e76\u540e\u5904\u7406\u91cf' },
      { typeName: 'Dataset', attrName: 'schema_version', valueType: 'String', required: true, description: '\u6a21\u5f0f\u7248\u672c' },
      { typeName: 'Dataset', attrName: 'size_mb', valueType: 'Number', required: false, description: '\u5927\u5c0f (MB)' },
    ],
  },
  {
    name: '\u5173\u7cfb\u5b9a\u4e49',
    columns: ['relName', 'fromType', 'toType', 'directed', 'description'],
    rows: [
      { relName: 'contained_in', fromType: 'Resource', toType: 'Resource', directed: true, description: '\u5305\u542b\u4e8e' },
      { relName: 'depends_on', fromType: 'ExecutableResource', toType: 'ExecutableResource', directed: true, description: '\u4f9d\u8d56\u4e8e' },
      { relName: 'produces', fromType: 'ExecutableResource', toType: 'Dataset', directed: true, description: '\u4ea7\u751f' },
    ],
  },
  { name: '\u5b9e\u4f53\u6570\u636e', columns: defaultSheets.entities.columns, rows: defaultSheets.entities.rows },
  { name: '\u5c5e\u6027\u6570\u636e', columns: defaultSheets.properties.columns, rows: defaultSheets.properties.rows },
  { name: '\u8fb9\u6570\u636e', columns: defaultSheets.edges.columns, rows: defaultSheets.edges.rows },
];

const queries = [
  {
    queryId: 'dslQuery',
    label: '\u591a\u6001\u8d44\u6e90\u5217\u8868',
    meaning: '\u67e5\u8be2 Resource \u53ca\u5176\u5b50\u7c7b\u578b\uff0c\u663e\u793a\u5b9e\u9645\u7c7b\u578b\u4e0e\u8d44\u6e90\u6807\u8bc6',
    dsl: "// DSL v2: polymorphic resource list\nconst q = dsl.query()\n  .select(['id', 'label', 'typeName', 'resourceKey'])\n  .fromStored('om_entity', { id: dsl.var('id'), type_name: dsl.var('typeName'), label: dsl.var('label') })\n  .fromStored('om_property', { entity_id: dsl.var('id'), attr_name: dsl.param('key_attr', 'resource_key'), value: dsl.var('resourceKey') })\n  .where(dsl.or(\n    dsl.eq(dsl.var('typeName'), dsl.param('t1', 'Resource')),\n    dsl.eq(dsl.var('typeName'), dsl.param('t2', 'ApiService')),\n    dsl.eq(dsl.var('typeName'), dsl.param('t3', 'Worker')),\n    dsl.eq(dsl.var('typeName'), dsl.param('t4', 'Dataset')),\n  ))\n  .order('typeName')\n  .build();",
    defaultView: 'table',
    kind: 'dsl',
    run: async (db) => {
      const q = dsl.query()
        .select(['id', 'label', 'typeName', 'resourceKey'])
        .fromStored('om_entity', { id: dsl.var('id'), type_name: dsl.var('typeName'), label: dsl.var('label') })
        .fromStored('om_property', { entity_id: dsl.var('id'), attr_name: dsl.param('key_attr', 'resource_key'), value: dsl.var('resourceKey') })
        .where(dsl.or(
          dsl.eq(dsl.var('typeName'), dsl.param('t1', 'Resource')),
          dsl.eq(dsl.var('typeName'), dsl.param('t2', 'ApiService')),
          dsl.eq(dsl.var('typeName'), dsl.param('t3', 'Worker')),
          dsl.eq(dsl.var('typeName'), dsl.param('t4', 'Dataset')),
        ))
        .order('typeName')
        .build();
      const result = await db.run(q.script, q.params);
      const columns = ['id', 'label', 'typeName', 'resourceKey'];
      const rows = (result.rows || []).map(row => Object.fromEntries(columns.map((column, index) => [column, row[index]])));
      return { view: 'table', kind: 'dsl', data: { columns, rows }, meta: {} };
    },
  },
  {
    queryId: 'impactAnalysis',
    label: '\u8d44\u6e90\u4f9d\u8d56\u5f71\u54cd\u5206\u6790',
    meaning: '\u4ece Gateway API \u51fa\u53d1\uff0c\u8ffd\u8e2a\u4f9d\u8d56\u4e0e\u4ea7\u751f\u7684\u6570\u636e',
    dsl: "await om.impactAnalysis(db, { rootId: 'api:gateway', relNames: ['depends_on', 'produces'], maxDepth: 3, direction: 'outgoing' });",
    defaultView: 'graph',
    kind: 'template',
    run: async (db) => {
      const result = await om.impactAnalysis(db, { rootId: 'api:gateway', relNames: ['depends_on', 'produces'], maxDepth: 3, direction: 'outgoing' });
      return { view: 'graph', kind: 'template', data: result.data.visual, meta: result.stats };
    },
  },
  {
    queryId: 'ownershipTree',
    label: '\u8d44\u6e90\u8303\u56f4\u6811',
    meaning: 'Platform Scope \u4e0b\u7684\u8d44\u6e90\u5c42\u7ea7',
    dsl: "await om.ownershipTree(db, { rootId: 'scope:platform', ownerRelNames: ['contained_in'], maxDepth: 3 });",
    defaultView: 'tree',
    kind: 'template',
    run: async (db) => {
      const result = await om.ownershipTree(db, { rootId: 'scope:platform', ownerRelNames: ['contained_in'], maxDepth: 3 });
      return { view: 'tree', kind: 'template', data: result.data.visual, meta: result.stats };
    },
  },
  {
    queryId: 'riskHotspot',
    label: '\u6570\u636e\u96c6\u5bb9\u91cf\u98ce\u9669\u70ed\u70b9',
    meaning: '\u6309 size_mb \u4e0e\u5173\u8054\u5ea6\u5bf9\u6570\u636e\u96c6\u8fdb\u884c\u6392\u5e8f',
    dsl: "await om.riskHotspot(db, { typeName: 'Dataset', riskAttr: 'size_mb', topK: 5, minScore: 0, degreeWeight: 10 });",
    defaultView: 'table',
    kind: 'template',
    run: async (db) => {
      const result = await om.riskHotspot(db, { typeName: 'Dataset', riskAttr: 'size_mb', topK: 5, minScore: 0, degreeWeight: 10 });
      return { view: 'table', kind: 'template', data: result.data.visual, meta: result.stats };
    },
  },
];

module.exports = {
  demoId: 'resource-graph',
  label: '\u901a\u7528\u8d44\u6e90\u56fe\uff08\u7ee7\u627f\uff09',
  defineOntology,
  defaultSheets,
  defaultTables,
  queries,
};
