/**
 * Procurement demo — ontology, seed data, queries.
 */
const { dsl, om } = require('cozo-lib-bun');

// ── Ontology setup ──────────────────────────────────────────────────────────

async function defineOntology(db) {
  await om.initSchema(db);

  await om.defineType(db, 'Supplier', '提供商品或服务的供应商');
  await om.defineType(db, 'PurchaseOrder', '向供应商下达的采购订单');
  await om.defineType(db, 'LineItem', '采购订单中的单个行项目');
  await om.defineType(db, 'Warehouse', '货物的实体仓储地点');
  await om.defineType(db, 'Contract', '管理采购条款的法律协议');

  await om.defineAttribute(db, 'Supplier', 'rating', 'Number', true, '供应商评分');
  await om.defineAttribute(db, 'Supplier', 'country', 'String', true, '国家/地区');
  await om.defineAttribute(db, 'Supplier', 'contact_email', 'String', false, '联系邮箱');

  await om.defineAttribute(db, 'PurchaseOrder', 'total_amount', 'Number', true, '订单总金额');
  await om.defineAttribute(db, 'PurchaseOrder', 'status', 'String', true, '订单状态');
  await om.defineAttribute(db, 'PurchaseOrder', 'order_date', 'String', false, '下单日期');

  await om.defineAttribute(db, 'LineItem', 'unit_price', 'Number', true, '单价');
  await om.defineAttribute(db, 'LineItem', 'quantity', 'Number', true, '数量');
  await om.defineAttribute(db, 'LineItem', 'sku', 'String', false, 'SKU');

  await om.defineAttribute(db, 'Warehouse', 'capacity', 'Number', true, '仓储容量');
  await om.defineAttribute(db, 'Warehouse', 'location', 'String', true, '仓库位置');

  await om.defineAttribute(db, 'Contract', 'risk_score', 'Number', true, '风险评分');
  await om.defineAttribute(db, 'Contract', 'terms_json', 'Json', true, '合同条款(JSON)');
  await om.defineAttribute(db, 'Contract', 'valid_until', 'String', false, '有效期至');

  await om.defineRelation(db, 'placed_with', 'PurchaseOrder', 'Supplier', true, '下单给');
  await om.defineRelation(db, 'has_line_item', 'PurchaseOrder', 'LineItem', true, '包含行项目');
  await om.defineRelation(db, 'fulfilled_by', 'LineItem', 'Warehouse', true, '由仓库履约');
  await om.defineRelation(db, 'covered_by', 'PurchaseOrder', 'Contract', true, '受合同覆盖');
  await om.defineRelation(db, 'supplies', 'Supplier', 'Warehouse', true, '供应给');
}

// ── Default seed sheets ─────────────────────────────────────────────────────

const defaultSheets = {
  entities: {
    columns: ['id', 'typeName', 'label'],
    rows: [
      { id: 's:acme', typeName: 'Supplier', label: '先达公司' },
      { id: 's:globex', typeName: 'Supplier', label: '环宇工业' },
      { id: 's:initech', typeName: 'Supplier', label: '启泰有限公司' },
      { id: 'po:1001', typeName: 'PurchaseOrder', label: '采购单-1001' },
      { id: 'po:1002', typeName: 'PurchaseOrder', label: '采购单-1002' },
      { id: 'po:1003', typeName: 'PurchaseOrder', label: '采购单-1003' },
      { id: 'li:a', typeName: 'LineItem', label: '钢梁 x200' },
      { id: 'li:b', typeName: 'LineItem', label: '铜线 x500' },
      { id: 'li:c', typeName: 'LineItem', label: '电路板 x1000' },
      { id: 'li:d', typeName: 'LineItem', label: '橡胶垫圈 x300' },
      { id: 'wh:east', typeName: 'Warehouse', label: '华东仓储中心' },
      { id: 'wh:west', typeName: 'Warehouse', label: '华西仓储中心' },
      { id: 'ct:master', typeName: 'Contract', label: '主供应协议' },
      { id: 'ct:spot', typeName: 'Contract', label: '现货采购合同' },
    ],
  },
  properties: {
    columns: ['entityId', 'attrName', 'value'],
    rows: [
      { entityId: 's:acme', attrName: 'rating', value: 4.5 },
      { entityId: 's:acme', attrName: 'country', value: 'US' },
      { entityId: 's:acme', attrName: 'contact_email', value: 'sales@acme.example' },
      { entityId: 's:globex', attrName: 'rating', value: 3.8 },
      { entityId: 's:globex', attrName: 'country', value: 'DE' },
      { entityId: 's:initech', attrName: 'rating', value: 2.1 },
      { entityId: 's:initech', attrName: 'country', value: 'CN' },
      { entityId: 'po:1001', attrName: 'total_amount', value: 54000 },
      { entityId: 'po:1001', attrName: 'status', value: 'approved' },
      { entityId: 'po:1001', attrName: 'order_date', value: '2026-01-15' },
      { entityId: 'po:1002', attrName: 'total_amount', value: 12750 },
      { entityId: 'po:1002', attrName: 'status', value: 'pending' },
      { entityId: 'po:1003', attrName: 'total_amount', value: 87200 },
      { entityId: 'po:1003', attrName: 'status', value: 'approved' },
      { entityId: 'li:a', attrName: 'unit_price', value: 270 },
      { entityId: 'li:a', attrName: 'quantity', value: 200 },
      { entityId: 'li:b', attrName: 'unit_price', value: 25.5 },
      { entityId: 'li:b', attrName: 'quantity', value: 500 },
      { entityId: 'li:c', attrName: 'unit_price', value: 87.2 },
      { entityId: 'li:c', attrName: 'quantity', value: 1000 },
      { entityId: 'li:d', attrName: 'unit_price', value: 4.25 },
      { entityId: 'li:d', attrName: 'quantity', value: 300 },
      { entityId: 'wh:east', attrName: 'capacity', value: 50000 },
      { entityId: 'wh:east', attrName: 'location', value: '江苏南京' },
      { entityId: 'wh:west', attrName: 'capacity', value: 35000 },
      { entityId: 'wh:west', attrName: 'location', value: '四川成都' },
      { entityId: 'ct:master', attrName: 'risk_score', value: 15 },
      { entityId: 'ct:master', attrName: 'terms_json', value: { duration_months: 24, penalty_pct: 5, auto_renew: true } },
      { entityId: 'ct:master', attrName: 'valid_until', value: '2028-01-01' },
      { entityId: 'ct:spot', attrName: 'risk_score', value: 42 },
      { entityId: 'ct:spot', attrName: 'terms_json', value: { duration_months: 3, penalty_pct: 0, auto_renew: false } },
    ],
  },
  edges: {
    columns: ['fromId', 'relName', 'toId', 'props'],
    rows: [
      { fromId: 'po:1001', relName: 'placed_with', toId: 's:acme', props: { buyer: '采购一组' } },
      { fromId: 'po:1002', relName: 'placed_with', toId: 's:globex', props: { buyer: '采购二组' } },
      { fromId: 'po:1003', relName: 'placed_with', toId: 's:initech', props: { buyer: '采购一组' } },
      { fromId: 'po:1001', relName: 'has_line_item', toId: 'li:a', props: { seq: 1 } },
      { fromId: 'po:1001', relName: 'has_line_item', toId: 'li:b', props: { seq: 2 } },
      { fromId: 'po:1002', relName: 'has_line_item', toId: 'li:c', props: { seq: 1 } },
      { fromId: 'po:1003', relName: 'has_line_item', toId: 'li:d', props: { seq: 1 } },
      { fromId: 'li:a', relName: 'fulfilled_by', toId: 'wh:east', props: { eta_days: 5 } },
      { fromId: 'li:b', relName: 'fulfilled_by', toId: 'wh:west', props: { eta_days: 3 } },
      { fromId: 'li:c', relName: 'fulfilled_by', toId: 'wh:east', props: { eta_days: 7 } },
      { fromId: 'li:d', relName: 'fulfilled_by', toId: 'wh:west', props: { eta_days: 2 } },
      { fromId: 'po:1001', relName: 'covered_by', toId: 'ct:master', props: { clause: '第四条甲款' } },
      { fromId: 'po:1003', relName: 'covered_by', toId: 'ct:spot', props: { clause: '附录乙' } },
      { fromId: 's:acme', relName: 'supplies', toId: 'wh:east', props: { since: '2024-06-01' } },
      { fromId: 's:globex', relName: 'supplies', toId: 'wh:west', props: { since: '2025-01-15' } },
      { fromId: 's:initech', relName: 'supplies', toId: 'wh:west', props: { since: '2025-09-01' } },
    ],
  },
};

const defaultTables = [
  {
    name: '类型定义',
    columns: ['typeName', 'parent_type', 'mixins', 'description'],
    rows: [
      { typeName: 'Supplier', parent_type: '', mixins: '', description: '提供商品或服务的供应商' },
      { typeName: 'PurchaseOrder', parent_type: '', mixins: '', description: '向供应商下达的采购订单' },
      { typeName: 'LineItem', parent_type: '', mixins: '', description: '采购订单中的单个行项目' },
      { typeName: 'Warehouse', parent_type: '', mixins: '', description: '货物的实体仓储地点' },
      { typeName: 'Contract', parent_type: '', mixins: '', description: '管理采购条款的法律协议' },
    ],
  },
  {
    name: '属性定义',
    columns: ['typeName', 'attrName', 'valueType', 'required', 'description'],
    rows: [
      { typeName: 'Supplier', attrName: 'rating', valueType: 'Number', required: true, description: '供应商评分' },
      { typeName: 'Supplier', attrName: 'country', valueType: 'String', required: true, description: '国家/地区' },
      { typeName: 'Supplier', attrName: 'contact_email', valueType: 'String', required: false, description: '联系邮箱' },

      { typeName: 'PurchaseOrder', attrName: 'total_amount', valueType: 'Number', required: true, description: '订单总金额' },
      { typeName: 'PurchaseOrder', attrName: 'status', valueType: 'String', required: true, description: '订单状态' },
      { typeName: 'PurchaseOrder', attrName: 'order_date', valueType: 'String', required: false, description: '下单日期' },

      { typeName: 'LineItem', attrName: 'unit_price', valueType: 'Number', required: true, description: '单价' },
      { typeName: 'LineItem', attrName: 'quantity', valueType: 'Number', required: true, description: '数量' },
      { typeName: 'LineItem', attrName: 'sku', valueType: 'String', required: false, description: 'SKU' },

      { typeName: 'Warehouse', attrName: 'capacity', valueType: 'Number', required: true, description: '仓储容量' },
      { typeName: 'Warehouse', attrName: 'location', valueType: 'String', required: true, description: '仓库位置' },

      { typeName: 'Contract', attrName: 'risk_score', valueType: 'Number', required: true, description: '风险评分' },
      { typeName: 'Contract', attrName: 'terms_json', valueType: 'Json', required: true, description: '合同条款(JSON)' },
      { typeName: 'Contract', attrName: 'valid_until', valueType: 'String', required: false, description: '有效期至' },
    ],
  },
  {
    name: '关系定义',
    columns: ['relName', 'fromType', 'toType', 'directed', 'description'],
    rows: [
      { relName: 'placed_with', fromType: 'PurchaseOrder', toType: 'Supplier', directed: true, description: '下单给' },
      { relName: 'has_line_item', fromType: 'PurchaseOrder', toType: 'LineItem', directed: true, description: '包含行项目' },
      { relName: 'fulfilled_by', fromType: 'LineItem', toType: 'Warehouse', directed: true, description: '由仓库履约' },
      { relName: 'covered_by', fromType: 'PurchaseOrder', toType: 'Contract', directed: true, description: '受合同覆盖' },
      { relName: 'supplies', fromType: 'Supplier', toType: 'Warehouse', directed: true, description: '供应给' },
    ],
  },
  {
    name: '实体数据',
    columns: defaultSheets.entities.columns,
    rows: defaultSheets.entities.rows,
  },
  {
    name: '属性数据',
    columns: defaultSheets.properties.columns,
    rows: defaultSheets.properties.rows,
  },
  {
    name: '边数据',
    columns: defaultSheets.edges.columns,
    rows: defaultSheets.edges.rows,
  },
];

// ── Query runners ───────────────────────────────────────────────────────────

const queries = [
  {
    queryId: 'dslQuery',
    label: '高价值行项目',
    meaning: '查找单价>20且数量>=200的行项目',
    dsl: `// DSL v2：高价值行项目\nconst q = dsl.query()\n  .select(['id', 'label', 'price', 'qty'])\n  .fromStored('om_entity', {\n    id: dsl.var('id'),\n    type_name: dsl.param('li_type', 'LineItem'),\n    label: dsl.var('label'),\n  })\n  .fromStored('om_property', {\n    entity_id: dsl.var('id'),\n    attr_name: dsl.param('price_attr', 'unit_price'),\n    value: dsl.var('price'),\n  })\n  .fromStored('om_property', {\n    entity_id: dsl.var('id'),\n    attr_name: dsl.param('qty_attr', 'quantity'),\n    value: dsl.var('qty'),\n  })\n  .where(dsl.and(\n    dsl.gt(dsl.var('price'), dsl.param('min_price', 20)),\n    dsl.gte(dsl.var('qty'), dsl.param('min_qty', 200)),\n  ))\n  .order('price')\n  .build();`,
    defaultView: 'table',
    kind: 'dsl',
    run: async (db) => {
      const q = dsl.query()
        .select(['id', 'label', 'price', 'qty'])
        .fromStored('om_entity', {
          id: dsl.var('id'),
          type_name: dsl.param('li_type', 'LineItem'),
          label: dsl.var('label'),
        })
        .fromStored('om_property', {
          entity_id: dsl.var('id'),
          attr_name: dsl.param('price_attr', 'unit_price'),
          value: dsl.var('price'),
        })
        .fromStored('om_property', {
          entity_id: dsl.var('id'),
          attr_name: dsl.param('qty_attr', 'quantity'),
          value: dsl.var('qty'),
        })
        .where(dsl.and(
          dsl.gt(dsl.var('price'), dsl.param('min_price', 20)),
          dsl.gte(dsl.var('qty'), dsl.param('min_qty', 200)),
        ))
        .order('price')
        .build();
      const result = await db.run(q.script, q.params);
      const columns = ['id', 'label', 'price', 'qty'];
      const rows = (result.rows || []).map(r =>
        Object.fromEntries(columns.map((c, i) => [c, r[i]]))
      );
      return { view: 'table', kind: 'dsl', data: { columns, rows }, meta: {} };
    },
  },
  {
    queryId: 'impactAnalysis',
    label: '采购单影响分析',
    meaning: '从 PO-1001 向外追踪影响范围',
    dsl: `// Template：影响分析（图）\nawait om.impactAnalysis(db, {\n  rootId: 'po:1001',\n  relNames: ['has_line_item', 'fulfilled_by', 'placed_with', 'covered_by'],\n  maxDepth: 3,\n  direction: 'outgoing',\n});`,
    defaultView: 'graph',
    kind: 'template',
    run: async (db) => {
      const result = await om.impactAnalysis(db, {
        rootId: 'po:1001',
        relNames: ['has_line_item', 'fulfilled_by', 'placed_with', 'covered_by'],
        maxDepth: 3,
        direction: 'outgoing',
      });
      return { view: 'graph', kind: 'template', data: result.data.visual, meta: result.stats };
    },
  },
  {
    queryId: 'ownershipTree',
    label: '供应商所有权树',
    meaning: '先达公司的供应链层级',
    dsl: `// Template：所有权树（树）\nawait om.ownershipTree(db, {\n  rootId: 's:acme',\n  ownerRelNames: ['supplies', 'placed_with'],\n  maxDepth: 3,\n});`,
    defaultView: 'tree',
    kind: 'template',
    run: async (db) => {
      const result = await om.ownershipTree(db, {
        rootId: 's:acme',
        ownerRelNames: ['supplies', 'placed_with'],
        maxDepth: 3,
      });
      return { view: 'tree', kind: 'template', data: result.data.visual, meta: result.stats };
    },
  },
  {
    queryId: 'riskHotspot',
    label: '合同风险热点',
    meaning: '按 risk_score 排列合同风险',
    dsl: `// Template：风险热点（榜单/表格）\nawait om.riskHotspot(db, {\n  typeName: 'Contract',\n  riskAttr: 'risk_score',\n  topK: 5,\n  minScore: 0,\n  degreeWeight: 1,\n});`,
    defaultView: 'table',
    kind: 'template',
    run: async (db) => {
      const result = await om.riskHotspot(db, {
        typeName: 'Contract',
        riskAttr: 'risk_score',
        topK: 5,
        minScore: 0,
        degreeWeight: 1,
      });
      return { view: 'table', kind: 'template', data: result.data.visual, meta: result.stats };
    },
  },
];

module.exports = {
  demoId: 'procurement',
  label: '采购管理',
  defineOntology,
  defaultSheets,
  defaultTables,
  queries,
};
