/**
 * CRM demo — ontology, seed data, queries.
 */
const { dsl, om } = require('cozo-lib-bun');

async function defineOntology(db) {
  await om.initSchema(db);

  await om.defineType(db, 'Account', '客户公司 / 账户');
  await om.defineType(db, 'Contact', '客户联系人');
  await om.defineType(db, 'Lead', '销售线索');
  await om.defineType(db, 'Opportunity', '销售商机');
  await om.defineType(db, 'Activity', '跟进活动');
  await om.defineType(db, 'SalesRep', '销售代表');

  await om.defineAttribute(db, 'Account', 'industry', 'String', true, '行业');
  await om.defineAttribute(db, 'Account', 'annual_revenue', 'Number', false, '年收入');
  await om.defineAttribute(db, 'Account', 'tier', 'String', false, '客户等级');

  await om.defineAttribute(db, 'Contact', 'email', 'String', true, '邮箱');
  await om.defineAttribute(db, 'Contact', 'phone', 'String', false, '电话');
  await om.defineAttribute(db, 'Contact', 'title', 'String', false, '职位');

  await om.defineAttribute(db, 'Lead', 'source', 'String', true, '来源');
  await om.defineAttribute(db, 'Lead', 'score', 'Number', false, '线索评分');
  await om.defineAttribute(db, 'Lead', 'status', 'String', false, '状态');

  await om.defineAttribute(db, 'Opportunity', 'amount', 'Number', true, '金额');
  await om.defineAttribute(db, 'Opportunity', 'stage', 'String', true, '阶段');
  await om.defineAttribute(db, 'Opportunity', 'close_probability', 'Number', false, '成交概率');

  await om.defineAttribute(db, 'Activity', 'activity_type', 'String', true, '活动类型');
  await om.defineAttribute(db, 'Activity', 'due_date', 'String', false, '截止日期');
  await om.defineAttribute(db, 'Activity', 'completed', 'Bool', false, '是否完成');

  await om.defineAttribute(db, 'SalesRep', 'region', 'String', true, '区域');
  await om.defineAttribute(db, 'SalesRep', 'quota', 'Number', false, '配额');
  await om.defineAttribute(db, 'SalesRep', 'active', 'Bool', false, '是否在职');

  await om.defineRelation(db, 'has_contact', 'Account', 'Contact', true, '拥有联系人');
  await om.defineRelation(db, 'has_opportunity', 'Account', 'Opportunity', true, '拥有商机');
  await om.defineRelation(db, 'owned_by', 'Opportunity', 'SalesRep', true, '归属销售');
  await om.defineRelation(db, 'has_activity', 'Opportunity', 'Activity', true, '包含活动');
  await om.defineRelation(db, 'assigned_to', 'Lead', 'SalesRep', true, '分配给');
  await om.defineRelation(db, 'converts_to', 'Lead', 'Opportunity', true, '转化为');
}

const defaultSheets = {
  entities: {
    columns: ['id', 'typeName', 'label'],
    rows: [
      { id: 'rep:li', typeName: 'SalesRep', label: '李雷' },
      { id: 'rep:wang', typeName: 'SalesRep', label: '王芳' },

      { id: 'acct:acme', typeName: 'Account', label: '先达客户' },
      { id: 'acct:globex', typeName: 'Account', label: '环宇客户' },

      { id: 'ct:alice', typeName: 'Contact', label: 'Alice（采购经理）' },
      { id: 'ct:bob', typeName: 'Contact', label: 'Bob（CTO）' },
      { id: 'ct:carol', typeName: 'Contact', label: 'Carol（财务）' },

      { id: 'lead:web-ship', typeName: 'Lead', label: '官网线索-航运' },
      { id: 'lead:expo-mfg', typeName: 'Lead', label: '会展线索-制造' },

      { id: 'opp:acme-renew', typeName: 'Opportunity', label: 'ACME 续费 2026' },
      { id: 'opp:globex-new', typeName: 'Opportunity', label: 'Globex 新签' },

      { id: 'act:call', typeName: 'Activity', label: '电话跟进' },
      { id: 'act:demo', typeName: 'Activity', label: '现场演示' },
      { id: 'act:quote', typeName: 'Activity', label: '报价确认' },
    ],
  },
  properties: {
    columns: ['entityId', 'attrName', 'value'],
    rows: [
      { entityId: 'rep:li', attrName: 'region', value: '华东' },
      { entityId: 'rep:li', attrName: 'quota', value: 1200000 },
      { entityId: 'rep:li', attrName: 'active', value: true },
      { entityId: 'rep:wang', attrName: 'region', value: '华北' },
      { entityId: 'rep:wang', attrName: 'quota', value: 900000 },
      { entityId: 'rep:wang', attrName: 'active', value: true },

      { entityId: 'acct:acme', attrName: 'industry', value: '航运物流' },
      { entityId: 'acct:acme', attrName: 'annual_revenue', value: 65000000 },
      { entityId: 'acct:acme', attrName: 'tier', value: 'A' },
      { entityId: 'acct:globex', attrName: 'industry', value: '智能制造' },
      { entityId: 'acct:globex', attrName: 'annual_revenue', value: 42000000 },
      { entityId: 'acct:globex', attrName: 'tier', value: 'B' },

      { entityId: 'ct:alice', attrName: 'email', value: 'alice@acme.example' },
      { entityId: 'ct:alice', attrName: 'phone', value: '+86-10-5555-0101' },
      { entityId: 'ct:alice', attrName: 'title', value: '采购经理' },
      { entityId: 'ct:bob', attrName: 'email', value: 'bob@acme.example' },
      { entityId: 'ct:bob', attrName: 'title', value: 'CTO' },
      { entityId: 'ct:carol', attrName: 'email', value: 'carol@globex.example' },
      { entityId: 'ct:carol', attrName: 'title', value: '财务' },

      { entityId: 'lead:web-ship', attrName: 'source', value: 'website' },
      { entityId: 'lead:web-ship', attrName: 'score', value: 78 },
      { entityId: 'lead:web-ship', attrName: 'status', value: 'qualified' },
      { entityId: 'lead:expo-mfg', attrName: 'source', value: 'expo' },
      { entityId: 'lead:expo-mfg', attrName: 'score', value: 64 },
      { entityId: 'lead:expo-mfg', attrName: 'status', value: 'new' },

      { entityId: 'opp:acme-renew', attrName: 'amount', value: 88000 },
      { entityId: 'opp:acme-renew', attrName: 'stage', value: 'proposal' },
      { entityId: 'opp:acme-renew', attrName: 'close_probability', value: 0.65 },
      { entityId: 'opp:globex-new', attrName: 'amount', value: 145000 },
      { entityId: 'opp:globex-new', attrName: 'stage', value: 'negotiation' },
      { entityId: 'opp:globex-new', attrName: 'close_probability', value: 0.5 },

      { entityId: 'act:call', attrName: 'activity_type', value: 'call' },
      { entityId: 'act:call', attrName: 'due_date', value: '2026-03-02' },
      { entityId: 'act:call', attrName: 'completed', value: true },
      { entityId: 'act:demo', attrName: 'activity_type', value: 'demo' },
      { entityId: 'act:demo', attrName: 'due_date', value: '2026-03-10' },
      { entityId: 'act:demo', attrName: 'completed', value: false },
      { entityId: 'act:quote', attrName: 'activity_type', value: 'quote' },
      { entityId: 'act:quote', attrName: 'due_date', value: '2026-03-15' },
      { entityId: 'act:quote', attrName: 'completed', value: false },
    ],
  },
  edges: {
    columns: ['fromId', 'relName', 'toId', 'props'],
    rows: [
      { fromId: 'acct:acme', relName: 'has_contact', toId: 'ct:alice', props: { primary: true } },
      { fromId: 'acct:acme', relName: 'has_contact', toId: 'ct:bob', props: { primary: false } },
      { fromId: 'acct:globex', relName: 'has_contact', toId: 'ct:carol', props: { primary: true } },

      { fromId: 'acct:acme', relName: 'has_opportunity', toId: 'opp:acme-renew', props: { fiscal_year: 2026 } },
      { fromId: 'acct:globex', relName: 'has_opportunity', toId: 'opp:globex-new', props: { fiscal_year: 2026 } },

      { fromId: 'opp:acme-renew', relName: 'owned_by', toId: 'rep:li', props: { role: 'owner' } },
      { fromId: 'opp:globex-new', relName: 'owned_by', toId: 'rep:wang', props: { role: 'owner' } },

      { fromId: 'opp:acme-renew', relName: 'has_activity', toId: 'act:call', props: { notes: '确认续费范围' } },
      { fromId: 'opp:acme-renew', relName: 'has_activity', toId: 'act:demo', props: { notes: '演示 ROI 模型' } },
      { fromId: 'opp:globex-new', relName: 'has_activity', toId: 'act:quote', props: { notes: '报价与条款确认' } },

      { fromId: 'lead:web-ship', relName: 'assigned_to', toId: 'rep:li', props: { since: '2026-02-18' } },
      { fromId: 'lead:expo-mfg', relName: 'assigned_to', toId: 'rep:wang', props: { since: '2026-02-20' } },
      { fromId: 'lead:web-ship', relName: 'converts_to', toId: 'opp:acme-renew', props: { converted_on: '2026-02-25' } },
    ],
  },
};

const defaultTables = [
  {
    name: '类型定义',
    columns: ['typeName', 'parent_type', 'mixins', 'description'],
    rows: [
      { typeName: 'Account', parent_type: '', mixins: '', description: '客户公司 / 账户' },
      { typeName: 'Contact', parent_type: '', mixins: '', description: '客户联系人' },
      { typeName: 'Lead', parent_type: '', mixins: '', description: '销售线索' },
      { typeName: 'Opportunity', parent_type: '', mixins: '', description: '销售商机' },
      { typeName: 'Activity', parent_type: '', mixins: '', description: '跟进活动' },
      { typeName: 'SalesRep', parent_type: '', mixins: '', description: '销售代表' },
    ],
  },
  {
    name: '属性定义',
    columns: ['typeName', 'attrName', 'valueType', 'required', 'description'],
    rows: [
      { typeName: 'Account', attrName: 'industry', valueType: 'String', required: true, description: '行业' },
      { typeName: 'Account', attrName: 'annual_revenue', valueType: 'Number', required: false, description: '年收入' },
      { typeName: 'Account', attrName: 'tier', valueType: 'String', required: false, description: '客户等级' },

      { typeName: 'Contact', attrName: 'email', valueType: 'String', required: true, description: '邮箱' },
      { typeName: 'Contact', attrName: 'phone', valueType: 'String', required: false, description: '电话' },
      { typeName: 'Contact', attrName: 'title', valueType: 'String', required: false, description: '职位' },

      { typeName: 'Lead', attrName: 'source', valueType: 'String', required: true, description: '来源' },
      { typeName: 'Lead', attrName: 'score', valueType: 'Number', required: false, description: '线索评分' },
      { typeName: 'Lead', attrName: 'status', valueType: 'String', required: false, description: '状态' },

      { typeName: 'Opportunity', attrName: 'amount', valueType: 'Number', required: true, description: '金额' },
      { typeName: 'Opportunity', attrName: 'stage', valueType: 'String', required: true, description: '阶段' },
      { typeName: 'Opportunity', attrName: 'close_probability', valueType: 'Number', required: false, description: '成交概率' },

      { typeName: 'Activity', attrName: 'activity_type', valueType: 'String', required: true, description: '活动类型' },
      { typeName: 'Activity', attrName: 'due_date', valueType: 'String', required: false, description: '截止日期' },
      { typeName: 'Activity', attrName: 'completed', valueType: 'Bool', required: false, description: '是否完成' },

      { typeName: 'SalesRep', attrName: 'region', valueType: 'String', required: true, description: '区域' },
      { typeName: 'SalesRep', attrName: 'quota', valueType: 'Number', required: false, description: '配额' },
      { typeName: 'SalesRep', attrName: 'active', valueType: 'Bool', required: false, description: '是否在职' },
    ],
  },
  {
    name: '关系定义',
    columns: ['relName', 'fromType', 'toType', 'directed', 'description'],
    rows: [
      { relName: 'has_contact', fromType: 'Account', toType: 'Contact', directed: true, description: '拥有联系人' },
      { relName: 'has_opportunity', fromType: 'Account', toType: 'Opportunity', directed: true, description: '拥有商机' },
      { relName: 'owned_by', fromType: 'Opportunity', toType: 'SalesRep', directed: true, description: '归属销售' },
      { relName: 'has_activity', fromType: 'Opportunity', toType: 'Activity', directed: true, description: '包含活动' },
      { relName: 'assigned_to', fromType: 'Lead', toType: 'SalesRep', directed: true, description: '分配给' },
      { relName: 'converts_to', fromType: 'Lead', toType: 'Opportunity', directed: true, description: '转化为' },
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

const queries = [
  {
    queryId: 'dslQuery',
    label: '高金额商机查询',
    meaning: '查找 amount>60000 且 stage!=closed_lost 的商机',
    dsl: `// DSL v2：高金额商机（amount>60000 且排除失败）\nconst q = dsl.query()\n  .select(['id', 'label', 'amount', 'stage'])\n  .fromStored('om_entity', {\n    id: dsl.var('id'),\n    type_name: dsl.param('type', 'Opportunity'),\n    label: dsl.var('label'),\n  })\n  .fromStored('om_property', {\n    entity_id: dsl.var('id'),\n    attr_name: dsl.param('amount_attr', 'amount'),\n    value: dsl.var('amount'),\n  })\n  .fromStored('om_property', {\n    entity_id: dsl.var('id'),\n    attr_name: dsl.param('stage_attr', 'stage'),\n    value: dsl.var('stage'),\n  })\n  .where(dsl.and(\n    dsl.gt(dsl.var('amount'), dsl.param('min_amount', 60000)),\n    dsl.not(dsl.eq(dsl.var('stage'), dsl.param('exclude', 'closed_lost'))),\n  ))\n  .order('amount')\n  .build();`,
    defaultView: 'table',
    kind: 'dsl',
    run: async (db) => {
      const q = dsl.query()
        .select(['id', 'label', 'amount', 'stage'])
        .fromStored('om_entity', {
          id: dsl.var('id'),
          type_name: dsl.param('type', 'Opportunity'),
          label: dsl.var('label'),
        })
        .fromStored('om_property', {
          entity_id: dsl.var('id'),
          attr_name: dsl.param('amount_attr', 'amount'),
          value: dsl.var('amount'),
        })
        .fromStored('om_property', {
          entity_id: dsl.var('id'),
          attr_name: dsl.param('stage_attr', 'stage'),
          value: dsl.var('stage'),
        })
        .where(dsl.and(
          dsl.gt(dsl.var('amount'), dsl.param('min_amount', 60000)),
          dsl.not(dsl.eq(dsl.var('stage'), dsl.param('exclude', 'closed_lost'))),
        ))
        .order('amount')
        .build();
      const result = await db.run(q.script, q.params);
      const columns = ['id', 'label', 'amount', 'stage'];
      const rows = (result.rows || []).map(r =>
        Object.fromEntries(columns.map((c, i) => [c, r[i]]))
      );
      return { view: 'table', kind: 'dsl', data: { columns, rows }, meta: {} };
    },
  },
  {
    queryId: 'impactAnalysis',
    label: '客户影响分析',
    meaning: '从先达客户出发追踪商机、负责人、活动与联系人',
    dsl: `// Template：影响分析（图）\nawait om.impactAnalysis(db, {\n  rootId: 'acct:acme',\n  relNames: ['has_opportunity', 'owned_by', 'has_activity', 'has_contact'],\n  maxDepth: 3,\n  direction: 'outgoing',\n});`,
    defaultView: 'graph',
    kind: 'template',
    run: async (db) => {
      const result = await om.impactAnalysis(db, {
        rootId: 'acct:acme',
        relNames: ['has_opportunity', 'owned_by', 'has_activity', 'has_contact'],
        maxDepth: 3,
        direction: 'outgoing',
      });
      return { view: 'graph', kind: 'template', data: result.data.visual, meta: result.stats };
    },
  },
  {
    queryId: 'ownershipTree',
    label: '客户所有权树',
    meaning: '先达客户 → 商机 → 活动 / 负责人',
    dsl: `// Template：所有权树（树）\nawait om.ownershipTree(db, {\n  rootId: 'acct:acme',\n  ownerRelNames: ['has_contact', 'has_opportunity', 'owned_by', 'has_activity'],\n  maxDepth: 3,\n});`,
    defaultView: 'tree',
    kind: 'template',
    run: async (db) => {
      const result = await om.ownershipTree(db, {
        rootId: 'acct:acme',
        ownerRelNames: ['has_contact', 'has_opportunity', 'owned_by', 'has_activity'],
        maxDepth: 3,
      });
      return { view: 'tree', kind: 'template', data: result.data.visual, meta: result.stats };
    },
  },
  {
    queryId: 'riskHotspot',
    label: '商机金额风险热点',
    meaning: '按 amount + 关联度评估商机风险',
    dsl: `// Template：风险热点（榜单/表格）\nawait om.riskHotspot(db, {\n  typeName: 'Opportunity',\n  riskAttr: 'amount',\n  topK: 5,\n  minScore: 0,\n  degreeWeight: 1000,\n});`,
    defaultView: 'table',
    kind: 'template',
    run: async (db) => {
      const result = await om.riskHotspot(db, {
        typeName: 'Opportunity',
        riskAttr: 'amount',
        topK: 5,
        minScore: 0,
        degreeWeight: 1000,
      });
      return { view: 'table', kind: 'template', data: result.data.visual, meta: result.stats };
    },
  },
];

module.exports = {
  demoId: 'crm',
  label: 'CRM 销售',
  defineOntology,
  defaultSheets,
  defaultTables,
  queries,
};
