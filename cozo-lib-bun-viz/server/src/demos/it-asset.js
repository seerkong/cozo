/**
 * IT Asset demo -- type hierarchy (inheritance), mixin, polymorphic query.
 */
const { dsl, om } = require('cozo-lib-bun');

// -- Ontology setup -----------------------------------------------------------

async function defineOntology(db) {
  await om.initSchema(db);

  await om.defineMixin(db, 'Auditable', '\u6df7\u5165: \u5ba1\u8ba1\u8ddf\u8e2a\u5b57\u6bb5');

  // Root type
  await om.defineType(db, 'Asset', '\u7ec4\u7ec7\u8d44\u4ea7\u6839\u7c7b\u578b', { mixins: ['Auditable'] });
  await om.defineType(db, 'ITAsset', 'IT\u8bbe\u5907', { parentType: 'Asset', mixins: ['Auditable'] });
  await om.defineType(db, 'Vehicle', '\u516c\u53f8\u8f66\u8f86', { parentType: 'Asset' });
  await om.defineType(db, 'Server', '\u7269\u7406/\u865a\u62df\u670d\u52a1\u5668', { parentType: 'ITAsset' });
  await om.defineType(db, 'Laptop', '\u5458\u5de5\u7b14\u8bb0\u672c', { parentType: 'ITAsset' });

  // Asset attrs (inherited by subtypes conceptually)
  await om.defineAttribute(db, 'Asset', 'asset_tag', 'String', true, '\u8d44\u4ea7\u7f16\u53f7');
  await om.defineAttribute(db, 'Asset', 'status', 'String', true, '\u8d44\u4ea7\u72b6\u6001');
  await om.defineAttribute(db, 'Asset', 'purchase_date', 'String', false, '\u91c7\u8d2d\u65e5\u671f');

  // ITAsset attrs
  await om.defineAttribute(db, 'ITAsset', 'ip_address', 'String', false, 'IP\u5730\u5740');
  await om.defineAttribute(db, 'ITAsset', 'os', 'String', false, '\u64cd\u4f5c\u7cfb\u7edf');

  // Server attrs
  await om.defineAttribute(db, 'Server', 'cpu_cores', 'Number', true, 'CPU\u6838\u5fc3\u6570');
  await om.defineAttribute(db, 'Server', 'ram_gb', 'Number', true, '\u5185\u5b58GB');

  // Laptop attrs
  await om.defineAttribute(db, 'Laptop', 'screen_size', 'Number', false, '\u5c4f\u5e55\u5c3a\u5bf8');
  await om.defineAttribute(db, 'Laptop', 'assigned_to', 'String', true, '\u5206\u914d\u7ed9');

  // Vehicle attrs
  await om.defineAttribute(db, 'Vehicle', 'plate_number', 'String', true, '\u8f66\u724c\u53f7');
  await om.defineAttribute(db, 'Vehicle', 'mileage', 'Number', false, '\u91cc\u7a0b\u6570');

  // Auditable mixin attrs
  await om.defineAttribute(db, 'Auditable', 'created_by', 'String', true, '\u521b\u5efa\u4eba');
  await om.defineAttribute(db, 'Auditable', 'updated_at', 'String', true, '\u66f4\u65b0\u65f6\u95f4');

  // Relations
  await om.defineRelation(db, 'located_in', 'Asset', 'Asset', true, '\u4f4d\u4e8e');
  await om.defineRelation(db, 'depends_on', 'Server', 'Server', true, '\u4f9d\u8d56\u4e8e');
  await om.defineRelation(db, 'assigned_laptop', 'Laptop', 'Asset', true, '\u5206\u914d\u7b14\u8bb0\u672c');
}

// -- Default seed sheets ------------------------------------------------------

const defaultSheets = {
  entities: {
    columns: ['id', 'typeName', 'label'],
    rows: [
      { id: 'srv:web01', typeName: 'Server', label: 'Web Server 01' },
      { id: 'srv:db01', typeName: 'Server', label: 'DB Server 01' },
      { id: 'srv:cache01', typeName: 'Server', label: 'Cache Server 01' },
      { id: 'lap:t01', typeName: 'Laptop', label: 'ThinkPad T14 #001' },
      { id: 'lap:m01', typeName: 'Laptop', label: 'MacBook Pro #001' },
      { id: 'veh:car01', typeName: 'Vehicle', label: 'Fleet Car A' },
      { id: 'veh:van01', typeName: 'Vehicle', label: 'Cargo Van B' },
      { id: 'dc:bj', typeName: 'Asset', label: 'Beijing DC' },
    ],
  },
  properties: {
    columns: ['entityId', 'attrName', 'value'],
    rows: [
      // Servers
      { entityId: 'srv:web01', attrName: 'asset_tag', value: 'SRV-2026-001' },
      { entityId: 'srv:web01', attrName: 'status', value: 'active' },
      { entityId: 'srv:web01', attrName: 'purchase_date', value: '2025-03-15' },
      { entityId: 'srv:web01', attrName: 'ip_address', value: '10.0.1.10' },
      { entityId: 'srv:web01', attrName: 'os', value: 'Ubuntu 22.04' },
      { entityId: 'srv:web01', attrName: 'cpu_cores', value: 32 },
      { entityId: 'srv:web01', attrName: 'ram_gb', value: 128 },
      { entityId: 'srv:web01', attrName: 'created_by', value: 'admin' },
      { entityId: 'srv:web01', attrName: 'updated_at', value: '2026-02-01' },

      { entityId: 'srv:db01', attrName: 'asset_tag', value: 'SRV-2026-002' },
      { entityId: 'srv:db01', attrName: 'status', value: 'active' },
      { entityId: 'srv:db01', attrName: 'ip_address', value: '10.0.1.20' },
      { entityId: 'srv:db01', attrName: 'os', value: 'Rocky Linux 9' },
      { entityId: 'srv:db01', attrName: 'cpu_cores', value: 64 },
      { entityId: 'srv:db01', attrName: 'ram_gb', value: 256 },
      { entityId: 'srv:db01', attrName: 'created_by', value: 'admin' },
      { entityId: 'srv:db01', attrName: 'updated_at', value: '2026-01-20' },

      { entityId: 'srv:cache01', attrName: 'asset_tag', value: 'SRV-2026-003' },
      { entityId: 'srv:cache01', attrName: 'status', value: 'maintenance' },
      { entityId: 'srv:cache01', attrName: 'cpu_cores', value: 16 },
      { entityId: 'srv:cache01', attrName: 'ram_gb', value: 64 },
      { entityId: 'srv:cache01', attrName: 'created_by', value: 'ops-bot' },
      { entityId: 'srv:cache01', attrName: 'updated_at', value: '2026-02-15' },

      // Laptops
      { entityId: 'lap:t01', attrName: 'asset_tag', value: 'LAP-2025-010' },
      { entityId: 'lap:t01', attrName: 'status', value: 'active' },
      { entityId: 'lap:t01', attrName: 'assigned_to', value: 'Zhang Wei' },
      { entityId: 'lap:t01', attrName: 'screen_size', value: 14 },
      { entityId: 'lap:t01', attrName: 'os', value: 'Windows 11' },
      { entityId: 'lap:t01', attrName: 'created_by', value: 'it-admin' },
      { entityId: 'lap:t01', attrName: 'updated_at', value: '2026-01-05' },

      { entityId: 'lap:m01', attrName: 'asset_tag', value: 'LAP-2025-011' },
      { entityId: 'lap:m01', attrName: 'status', value: 'active' },
      { entityId: 'lap:m01', attrName: 'assigned_to', value: 'Li Na' },
      { entityId: 'lap:m01', attrName: 'screen_size', value: 16 },
      { entityId: 'lap:m01', attrName: 'os', value: 'macOS Sonoma' },
      { entityId: 'lap:m01', attrName: 'created_by', value: 'it-admin' },
      { entityId: 'lap:m01', attrName: 'updated_at', value: '2026-02-10' },

      // Vehicles
      { entityId: 'veh:car01', attrName: 'asset_tag', value: 'VEH-2024-001' },
      { entityId: 'veh:car01', attrName: 'status', value: 'active' },
      { entityId: 'veh:car01', attrName: 'plate_number', value: 'BJ-A12345' },
      { entityId: 'veh:car01', attrName: 'mileage', value: 45200 },
      { entityId: 'veh:car01', attrName: 'created_by', value: 'fleet-mgr' },
      { entityId: 'veh:car01', attrName: 'updated_at', value: '2026-02-20' },

      { entityId: 'veh:van01', attrName: 'asset_tag', value: 'VEH-2024-002' },
      { entityId: 'veh:van01', attrName: 'status', value: 'retired' },
      { entityId: 'veh:van01', attrName: 'plate_number', value: 'BJ-B67890' },
      { entityId: 'veh:van01', attrName: 'mileage', value: 120300 },
      { entityId: 'veh:van01', attrName: 'created_by', value: 'fleet-mgr' },
      { entityId: 'veh:van01', attrName: 'updated_at', value: '2026-01-30' },

      // DC (plain Asset)
      { entityId: 'dc:bj', attrName: 'asset_tag', value: 'DC-BJ-001' },
      { entityId: 'dc:bj', attrName: 'status', value: 'active' },
      { entityId: 'dc:bj', attrName: 'created_by', value: 'admin' },
      { entityId: 'dc:bj', attrName: 'updated_at', value: '2025-12-01' },
    ],
  },
  edges: {
    columns: ['fromId', 'relName', 'toId', 'props'],
    rows: [
      { fromId: 'srv:web01', relName: 'located_in', toId: 'dc:bj', props: { rack: 'A-12' } },
      { fromId: 'srv:db01', relName: 'located_in', toId: 'dc:bj', props: { rack: 'A-14' } },
      { fromId: 'srv:cache01', relName: 'located_in', toId: 'dc:bj', props: { rack: 'B-02' } },
      { fromId: 'srv:web01', relName: 'depends_on', toId: 'srv:db01', props: { protocol: 'TCP/5432' } },
      { fromId: 'srv:web01', relName: 'depends_on', toId: 'srv:cache01', props: { protocol: 'TCP/6379' } },
      { fromId: 'lap:t01', relName: 'assigned_laptop', toId: 'dc:bj', props: { note: 'remote access' } },
    ],
  },
};

const defaultTables = [
  {
    name: '\u7c7b\u578b\u5b9a\u4e49',
    columns: ['typeName', 'parent_type', 'mixins', 'description'],
    rows: [
      { typeName: 'Asset', parent_type: '', mixins: 'Auditable', description: '\u7ec4\u7ec7\u8d44\u4ea7\u6839\u7c7b\u578b' },
      { typeName: 'ITAsset', parent_type: 'Asset', mixins: 'Auditable', description: 'IT\u8bbe\u5907' },
      { typeName: 'Server', parent_type: 'ITAsset', mixins: '', description: '\u7269\u7406/\u865a\u62df\u670d\u52a1\u5668' },
      { typeName: 'Laptop', parent_type: 'ITAsset', mixins: '', description: '\u5458\u5de5\u7b14\u8bb0\u672c' },
      { typeName: 'Vehicle', parent_type: 'Asset', mixins: '', description: '\u516c\u53f8\u8f66\u8f86' },
      { typeName: 'Auditable', parent_type: '', mixins: '', description: '\u6df7\u5165: \u5ba1\u8ba1\u8ddf\u8e2a\u5b57\u6bb5' },
    ],
  },
  {
    name: '\u5c5e\u6027\u5b9a\u4e49',
    columns: ['typeName', 'attrName', 'valueType', 'required', 'description'],
    rows: [
      { typeName: 'Asset', attrName: 'asset_tag', valueType: 'String', required: true, description: '\u8d44\u4ea7\u7f16\u53f7' },
      { typeName: 'Asset', attrName: 'status', valueType: 'String', required: true, description: '\u8d44\u4ea7\u72b6\u6001' },
      { typeName: 'Asset', attrName: 'purchase_date', valueType: 'String', required: false, description: '\u91c7\u8d2d\u65e5\u671f' },

      { typeName: 'ITAsset', attrName: 'ip_address', valueType: 'String', required: false, description: 'IP\u5730\u5740' },
      { typeName: 'ITAsset', attrName: 'os', valueType: 'String', required: false, description: '\u64cd\u4f5c\u7cfb\u7edf' },

      { typeName: 'Server', attrName: 'cpu_cores', valueType: 'Number', required: true, description: 'CPU\u6838\u5fc3\u6570' },
      { typeName: 'Server', attrName: 'ram_gb', valueType: 'Number', required: true, description: '\u5185\u5b58GB' },

      { typeName: 'Laptop', attrName: 'screen_size', valueType: 'Number', required: false, description: '\u5c4f\u5e55\u5c3a\u5bf8' },
      { typeName: 'Laptop', attrName: 'assigned_to', valueType: 'String', required: true, description: '\u5206\u914d\u7ed9' },

      { typeName: 'Vehicle', attrName: 'plate_number', valueType: 'String', required: true, description: '\u8f66\u724c\u53f7' },
      { typeName: 'Vehicle', attrName: 'mileage', valueType: 'Number', required: false, description: '\u91cc\u7a0b\u6570' },

      { typeName: 'Auditable', attrName: 'created_by', valueType: 'String', required: true, description: '\u521b\u5efa\u4eba' },
      { typeName: 'Auditable', attrName: 'updated_at', valueType: 'String', required: true, description: '\u66f4\u65b0\u65f6\u95f4' },
    ],
  },
  {
    name: '\u5173\u7cfb\u5b9a\u4e49',
    columns: ['relName', 'fromType', 'toType', 'directed', 'description'],
    rows: [
      { relName: 'located_in', fromType: 'Asset', toType: 'Asset', directed: true, description: '\u4f4d\u4e8e' },
      { relName: 'depends_on', fromType: 'Server', toType: 'Server', directed: true, description: '\u4f9d\u8d56\u4e8e' },
      { relName: 'assigned_laptop', fromType: 'Laptop', toType: 'Asset', directed: true, description: '\u5206\u914d\u7b14\u8bb0\u672c' },
    ],
  },
  {
    name: '\u5b9e\u4f53\u6570\u636e',
    columns: defaultSheets.entities.columns,
    rows: defaultSheets.entities.rows,
  },
  {
    name: '\u5c5e\u6027\u6570\u636e',
    columns: defaultSheets.properties.columns,
    rows: defaultSheets.properties.rows,
  },
  {
    name: '\u8fb9\u6570\u636e',
    columns: defaultSheets.edges.columns,
    rows: defaultSheets.edges.rows,
  },
];

// -- Query runners ------------------------------------------------------------

const queries = [
  {
    queryId: 'dslQuery',
    label: '\u591a\u6001\u8d44\u4ea7\u5217\u8868',
    meaning: '\u67e5\u8be2\u6240\u6709 Asset \u53ca\u5176\u5b50\u7c7b\u578b\uff08Server/Laptop/Vehicle\uff09\uff0c\u663e\u793a\u5b9e\u9645\u7c7b\u578b',
    dsl: "// DSL v2: polymorphic asset list\n// Queries om_entity for all types in the Asset hierarchy\nconst q = dsl.query()\n  .select(['id', 'label', 'typeName', 'tag'])\n  .fromStored('om_entity', {\n    id: dsl.var('id'),\n    type_name: dsl.var('typeName'),\n    label: dsl.var('label'),\n  })\n  .fromStored('om_property', {\n    entity_id: dsl.var('id'),\n    attr_name: dsl.param('tag_attr', 'asset_tag'),\n    value: dsl.var('tag'),\n  })\n  .where(dsl.or(\n    dsl.eq(dsl.var('typeName'), dsl.param('t1', 'Asset')),\n    dsl.eq(dsl.var('typeName'), dsl.param('t2', 'Server')),\n    dsl.eq(dsl.var('typeName'), dsl.param('t3', 'Laptop')),\n    dsl.eq(dsl.var('typeName'), dsl.param('t4', 'Vehicle')),\n  ))\n  .order('typeName')\n  .build();",
    defaultView: 'table',
    kind: 'dsl',
    run: async (db) => {
      const q = dsl.query()
        .select(['id', 'label', 'typeName', 'tag'])
        .fromStored('om_entity', {
          id: dsl.var('id'),
          type_name: dsl.var('typeName'),
          label: dsl.var('label'),
        })
        .fromStored('om_property', {
          entity_id: dsl.var('id'),
          attr_name: dsl.param('tag_attr', 'asset_tag'),
          value: dsl.var('tag'),
        })
        .where(dsl.or(
          dsl.eq(dsl.var('typeName'), dsl.param('t1', 'Asset')),
          dsl.eq(dsl.var('typeName'), dsl.param('t2', 'Server')),
          dsl.eq(dsl.var('typeName'), dsl.param('t3', 'Laptop')),
          dsl.eq(dsl.var('typeName'), dsl.param('t4', 'Vehicle')),
        ))
        .order('typeName')
        .build();
      const result = await db.run(q.script, q.params);
      const columns = ['id', 'label', 'typeName', 'tag'];
      const rows = (result.rows || []).map(r =>
        Object.fromEntries(columns.map((c, i) => [c, r[i]]))
      );
      return { view: 'table', kind: 'dsl', data: { columns, rows }, meta: {} };
    },
  },
  {
    queryId: 'impactAnalysis',
    label: '\u670d\u52a1\u5668\u4f9d\u8d56\u5f71\u54cd\u5206\u6790',
    meaning: '\u4ece Web Server 01 \u51fa\u53d1\uff0c\u6cbf depends_on / located_in \u8ffd\u8e2a\u5f71\u54cd\u8303\u56f4',
    dsl: "// Template: impact analysis (graph)\nawait om.impactAnalysis(db, {\n  rootId: 'srv:web01',\n  relNames: ['depends_on', 'located_in'],\n  maxDepth: 3,\n  direction: 'outgoing',\n});",
    defaultView: 'graph',
    kind: 'template',
    run: async (db) => {
      const result = await om.impactAnalysis(db, {
        rootId: 'srv:web01',
        relNames: ['depends_on', 'located_in'],
        maxDepth: 3,
        direction: 'outgoing',
      });
      return { view: 'graph', kind: 'template', data: result.data.visual, meta: result.stats };
    },
  },
  {
    queryId: 'ownershipTree',
    label: '\u6570\u636e\u4e2d\u5fc3\u8d44\u4ea7\u6811',
    meaning: 'Beijing DC \u2192 \u670d\u52a1\u5668 / \u7b14\u8bb0\u672c\u5c42\u7ea7',
    dsl: "// Template: ownership tree\nawait om.ownershipTree(db, {\n  rootId: 'dc:bj',\n  ownerRelNames: ['located_in', 'assigned_laptop'],\n  maxDepth: 3,\n});",
    defaultView: 'tree',
    kind: 'template',
    run: async (db) => {
      const result = await om.ownershipTree(db, {
        rootId: 'dc:bj',
        ownerRelNames: ['located_in', 'assigned_laptop'],
        maxDepth: 3,
      });
      return { view: 'tree', kind: 'template', data: result.data.visual, meta: result.stats };
    },
  },
  {
    queryId: 'riskHotspot',
    label: '\u670d\u52a1\u5668 RAM \u98ce\u9669\u70ed\u70b9',
    meaning: '\u6309 ram_gb + \u5173\u8054\u5ea6\u8bc4\u4f30\u670d\u52a1\u5668\u98ce\u9669',
    dsl: "// Template: risk hotspot (ranking table)\nawait om.riskHotspot(db, {\n  typeName: 'Server',\n  riskAttr: 'ram_gb',\n  topK: 5,\n  minScore: 0,\n  degreeWeight: 10,\n});",
    defaultView: 'table',
    kind: 'template',
    run: async (db) => {
      const result = await om.riskHotspot(db, {
        typeName: 'Server',
        riskAttr: 'ram_gb',
        topK: 5,
        minScore: 0,
        degreeWeight: 10,
      });
      return { view: 'table', kind: 'template', data: result.data.visual, meta: result.stats };
    },
  },
];

module.exports = {
  demoId: 'it-asset',
  label: 'IT\u8d44\u4ea7\u7ba1\u7406\uff08\u7ee7\u627f\uff09',
  defineOntology,
  defaultSheets,
  defaultTables,
  queries,
};
