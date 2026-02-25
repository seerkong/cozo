/**
 * HR demo — ontology, seed data, queries.
 */
const { dsl, om } = require('cozo-lib-bun');

async function defineOntology(db) {
  await om.initSchema(db);

  await om.defineType(db, 'Employee', '组织中的在职员工');
  await om.defineType(db, 'Department', '组织架构单元');
  await om.defineType(db, 'Position', '部门内的岗位角色');
  await om.defineType(db, 'Skill', '技术能力或软技能');
  await om.defineType(db, 'ReviewCycle', '周期性绩效考核');

  await om.defineAttribute(db, 'Employee', 'email', 'String', true, '邮箱');
  await om.defineAttribute(db, 'Employee', 'salary', 'Number', true, '薪资');
  await om.defineAttribute(db, 'Employee', 'performance_score', 'Number', false, '绩效评分');
  await om.defineAttribute(db, 'Employee', 'competency_json', 'Json', false, '能力画像(JSON)');

  await om.defineAttribute(db, 'Department', 'budget', 'Number', true, '预算');
  await om.defineAttribute(db, 'Department', 'headcount', 'Number', true, '编制人数');
  await om.defineAttribute(db, 'Department', 'location', 'String', false, '地点');

  await om.defineAttribute(db, 'Position', 'level', 'String', true, '职级');
  await om.defineAttribute(db, 'Position', 'min_salary', 'Number', false, '最低薪资');
  await om.defineAttribute(db, 'Position', 'max_salary', 'Number', false, '最高薪资');

  await om.defineAttribute(db, 'Skill', 'category', 'String', true, '技能类别');

  await om.defineAttribute(db, 'ReviewCycle', 'quarter', 'String', true, '季度');
  await om.defineAttribute(db, 'ReviewCycle', 'year', 'Number', true, '年份');
  await om.defineAttribute(db, 'ReviewCycle', 'status', 'String', false, '状态');

  await om.defineRelation(db, 'works_in', 'Employee', 'Department', true, '就职部门');
  await om.defineRelation(db, 'reports_to', 'Employee', 'Employee', true, '汇报给');
  await om.defineRelation(db, 'fills_position', 'Employee', 'Position', true, '担任岗位');
  await om.defineRelation(db, 'requires_skill', 'Position', 'Skill', true, '需要技能');
  await om.defineRelation(db, 'reviewed_in', 'Employee', 'ReviewCycle', true, '参与考核周期');
}

const defaultSheets = {
  entities: {
    columns: ['id', 'typeName', 'label'],
    rows: [
      { id: 'emp:alice', typeName: 'Employee', label: '陈晓琳' },
      { id: 'emp:bob', typeName: 'Employee', label: '马志远' },
      { id: 'emp:carol', typeName: 'Employee', label: '王思雨' },
      { id: 'emp:dave', typeName: 'Employee', label: '金大伟' },
      { id: 'emp:eve', typeName: 'Employee', label: '周怡然' },
      { id: 'emp:frank', typeName: 'Employee', label: '李峰' },
      { id: 'emp:grace', typeName: 'Employee', label: '朴恩惠' },
      { id: 'dept:eng', typeName: 'Department', label: '工程部' },
      { id: 'dept:product', typeName: 'Department', label: '产品部' },
      { id: 'pos:swe', typeName: 'Position', label: '软件工程师' },
      { id: 'pos:lead', typeName: 'Position', label: '技术负责人' },
      { id: 'pos:pm', typeName: 'Position', label: '产品经理' },
      { id: 'sk:js', typeName: 'Skill', label: 'JavaScript' },
      { id: 'sk:sys', typeName: 'Skill', label: '系统设计' },
      { id: 'rc:q1', typeName: 'ReviewCycle', label: '2026年第一季度考核' },
      { id: 'rc:q2', typeName: 'ReviewCycle', label: '2026年第二季度考核' },
    ],
  },
  properties: {
    columns: ['entityId', 'attrName', 'value'],
    rows: [
      { entityId: 'emp:alice', attrName: 'email', value: 'alice@acme.com' },
      { entityId: 'emp:alice', attrName: 'salary', value: 180000 },
      { entityId: 'emp:alice', attrName: 'performance_score', value: 4.5 },
      { entityId: 'emp:alice', attrName: 'competency_json', value: { leadership: 5, coding: 4 } },
      { entityId: 'emp:bob', attrName: 'email', value: 'bob@acme.com' },
      { entityId: 'emp:bob', attrName: 'salary', value: 150000 },
      { entityId: 'emp:bob', attrName: 'performance_score', value: 4.2 },
      { entityId: 'emp:bob', attrName: 'competency_json', value: { backend: 5, devops: 3 } },
      { entityId: 'emp:carol', attrName: 'email', value: 'carol@acme.com' },
      { entityId: 'emp:carol', attrName: 'salary', value: 145000 },
      { entityId: 'emp:carol', attrName: 'performance_score', value: 3.8 },
      { entityId: 'emp:dave', attrName: 'email', value: 'dave@acme.com' },
      { entityId: 'emp:dave', attrName: 'salary', value: 130000 },
      { entityId: 'emp:dave', attrName: 'performance_score', value: 3.5 },
      { entityId: 'emp:eve', attrName: 'email', value: 'eve@acme.com' },
      { entityId: 'emp:eve', attrName: 'salary', value: 160000 },
      { entityId: 'emp:eve', attrName: 'performance_score', value: 4.0 },
      { entityId: 'emp:eve', attrName: 'competency_json', value: { product: 5, analytics: 4 } },
      { entityId: 'emp:frank', attrName: 'email', value: 'frank@acme.com' },
      { entityId: 'emp:frank', attrName: 'salary', value: 125000 },
      { entityId: 'emp:grace', attrName: 'email', value: 'grace@acme.com' },
      { entityId: 'emp:grace', attrName: 'salary', value: 140000 },
      { entityId: 'emp:grace', attrName: 'performance_score', value: 4.1 },
      { entityId: 'dept:eng', attrName: 'budget', value: 2000000 },
      { entityId: 'dept:eng', attrName: 'headcount', value: 5 },
      { entityId: 'dept:eng', attrName: 'location', value: '北京' },
      { entityId: 'dept:product', attrName: 'budget', value: 800000 },
      { entityId: 'dept:product', attrName: 'headcount', value: 2 },
      { entityId: 'dept:product', attrName: 'location', value: '上海' },
      { entityId: 'pos:swe', attrName: 'level', value: 'IC3' },
      { entityId: 'pos:swe', attrName: 'min_salary', value: 120000 },
      { entityId: 'pos:swe', attrName: 'max_salary', value: 160000 },
      { entityId: 'pos:lead', attrName: 'level', value: 'IC5' },
      { entityId: 'pos:lead', attrName: 'min_salary', value: 160000 },
      { entityId: 'pos:lead', attrName: 'max_salary', value: 200000 },
      { entityId: 'pos:pm', attrName: 'level', value: 'IC4' },
      { entityId: 'pos:pm', attrName: 'min_salary', value: 140000 },
      { entityId: 'pos:pm', attrName: 'max_salary', value: 180000 },
      { entityId: 'sk:js', attrName: 'category', value: '编程语言' },
      { entityId: 'sk:sys', attrName: 'category', value: '架构设计' },
      { entityId: 'rc:q1', attrName: 'quarter', value: 'Q1' },
      { entityId: 'rc:q1', attrName: 'year', value: 2026 },
      { entityId: 'rc:q1', attrName: 'status', value: 'completed' },
      { entityId: 'rc:q2', attrName: 'quarter', value: 'Q2' },
      { entityId: 'rc:q2', attrName: 'year', value: 2026 },
      { entityId: 'rc:q2', attrName: 'status', value: 'in_progress' },
    ],
  },
  edges: {
    columns: ['fromId', 'relName', 'toId', 'props'],
    rows: [
      { fromId: 'emp:alice', relName: 'works_in', toId: 'dept:eng', props: { since: '2022-03-01' } },
      { fromId: 'emp:bob', relName: 'works_in', toId: 'dept:eng', props: { since: '2023-06-15' } },
      { fromId: 'emp:carol', relName: 'works_in', toId: 'dept:eng', props: { since: '2023-09-01' } },
      { fromId: 'emp:dave', relName: 'works_in', toId: 'dept:eng', props: { since: '2024-01-10' } },
      { fromId: 'emp:frank', relName: 'works_in', toId: 'dept:eng', props: { since: '2024-07-01' } },
      { fromId: 'emp:eve', relName: 'works_in', toId: 'dept:product', props: { since: '2022-11-01' } },
      { fromId: 'emp:grace', relName: 'works_in', toId: 'dept:product', props: { since: '2023-04-20' } },
      { fromId: 'emp:bob', relName: 'reports_to', toId: 'emp:alice', props: {} },
      { fromId: 'emp:carol', relName: 'reports_to', toId: 'emp:alice', props: {} },
      { fromId: 'emp:dave', relName: 'reports_to', toId: 'emp:bob', props: {} },
      { fromId: 'emp:frank', relName: 'reports_to', toId: 'emp:bob', props: {} },
      { fromId: 'emp:grace', relName: 'reports_to', toId: 'emp:eve', props: {} },
      { fromId: 'emp:alice', relName: 'fills_position', toId: 'pos:lead', props: { start: '2022-03-01' } },
      { fromId: 'emp:bob', relName: 'fills_position', toId: 'pos:lead', props: { start: '2023-06-15' } },
      { fromId: 'emp:carol', relName: 'fills_position', toId: 'pos:swe', props: { start: '2023-09-01' } },
      { fromId: 'emp:dave', relName: 'fills_position', toId: 'pos:swe', props: { start: '2024-01-10' } },
      { fromId: 'emp:eve', relName: 'fills_position', toId: 'pos:pm', props: { start: '2022-11-01' } },
      { fromId: 'pos:swe', relName: 'requires_skill', toId: 'sk:js', props: { proficiency: 'advanced' } },
      { fromId: 'pos:lead', relName: 'requires_skill', toId: 'sk:js', props: { proficiency: 'expert' } },
      { fromId: 'pos:lead', relName: 'requires_skill', toId: 'sk:sys', props: { proficiency: 'advanced' } },
      { fromId: 'pos:pm', relName: 'requires_skill', toId: 'sk:sys', props: { proficiency: 'intermediate' } },
      { fromId: 'emp:alice', relName: 'reviewed_in', toId: 'rc:q1', props: { rating: 4.5 } },
      { fromId: 'emp:bob', relName: 'reviewed_in', toId: 'rc:q1', props: { rating: 4.2 } },
      { fromId: 'emp:carol', relName: 'reviewed_in', toId: 'rc:q1', props: { rating: 3.8 } },
      { fromId: 'emp:eve', relName: 'reviewed_in', toId: 'rc:q1', props: { rating: 4.0 } },
    ],
  },
};

const defaultTables = [
  {
    name: '类型定义',
    columns: ['typeName', 'parent_type', 'mixins', 'description'],
    rows: [
      { typeName: 'Employee', parent_type: '', mixins: '', description: '组织中的在职员工' },
      { typeName: 'Department', parent_type: '', mixins: '', description: '组织架构单元' },
      { typeName: 'Position', parent_type: '', mixins: '', description: '部门内的岗位角色' },
      { typeName: 'Skill', parent_type: '', mixins: '', description: '技术能力或软技能' },
      { typeName: 'ReviewCycle', parent_type: '', mixins: '', description: '周期性绩效考核' },
    ],
  },
  {
    name: '属性定义',
    columns: ['typeName', 'attrName', 'valueType', 'required', 'description'],
    rows: [
      { typeName: 'Employee', attrName: 'email', valueType: 'String', required: true, description: '邮箱' },
      { typeName: 'Employee', attrName: 'salary', valueType: 'Number', required: true, description: '薪资' },
      { typeName: 'Employee', attrName: 'performance_score', valueType: 'Number', required: false, description: '绩效评分' },
      { typeName: 'Employee', attrName: 'competency_json', valueType: 'Json', required: false, description: '能力画像(JSON)' },

      { typeName: 'Department', attrName: 'budget', valueType: 'Number', required: true, description: '预算' },
      { typeName: 'Department', attrName: 'headcount', valueType: 'Number', required: true, description: '编制人数' },
      { typeName: 'Department', attrName: 'location', valueType: 'String', required: false, description: '地点' },

      { typeName: 'Position', attrName: 'level', valueType: 'String', required: true, description: '职级' },
      { typeName: 'Position', attrName: 'min_salary', valueType: 'Number', required: false, description: '最低薪资' },
      { typeName: 'Position', attrName: 'max_salary', valueType: 'Number', required: false, description: '最高薪资' },

      { typeName: 'Skill', attrName: 'category', valueType: 'String', required: true, description: '技能类别' },

      { typeName: 'ReviewCycle', attrName: 'quarter', valueType: 'String', required: true, description: '季度' },
      { typeName: 'ReviewCycle', attrName: 'year', valueType: 'Number', required: true, description: '年份' },
      { typeName: 'ReviewCycle', attrName: 'status', valueType: 'String', required: false, description: '状态' },
    ],
  },
  {
    name: '关系定义',
    columns: ['relName', 'fromType', 'toType', 'directed', 'description'],
    rows: [
      { relName: 'works_in', fromType: 'Employee', toType: 'Department', directed: true, description: '就职部门' },
      { relName: 'reports_to', fromType: 'Employee', toType: 'Employee', directed: true, description: '汇报给' },
      { relName: 'fills_position', fromType: 'Employee', toType: 'Position', directed: true, description: '担任岗位' },
      { relName: 'requires_skill', fromType: 'Position', toType: 'Skill', directed: true, description: '需要技能' },
      { relName: 'reviewed_in', fromType: 'Employee', toType: 'ReviewCycle', directed: true, description: '参与考核周期' },
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
    label: '高薪员工查询',
    meaning: '薪资>=140000的员工（DSL组合条件）',
    dsl: `// DSL v2：高薪员工（薪资>=140000）\nconst q = dsl.query()\n  .select(['id', 'label', 'salary'])\n  .fromStored('om_entity', {\n    id: dsl.var('id'),\n    type_name: dsl.param('emp_type', 'Employee'),\n    label: dsl.var('label'),\n  })\n  .fromStored('om_property', {\n    entity_id: dsl.var('id'),\n    attr_name: dsl.param('sal_attr', 'salary'),\n    value: dsl.var('salary'),\n  })\n  .where(dsl.gte(dsl.var('salary'), dsl.param('min_sal', 140000)))\n  .order('salary')\n  .build();`,
    defaultView: 'table',
    kind: 'dsl',
    run: async (db) => {
      const q = dsl.query()
        .select(['id', 'label', 'salary'])
        .fromStored('om_entity', {
          id: dsl.var('id'),
          type_name: dsl.param('emp_type', 'Employee'),
          label: dsl.var('label'),
        })
        .fromStored('om_property', {
          entity_id: dsl.var('id'),
          attr_name: dsl.param('sal_attr', 'salary'),
          value: dsl.var('salary'),
        })
        .where(dsl.gte(dsl.var('salary'), dsl.param('min_sal', 140000)))
        .order('salary')
        .build();
      const result = await db.run(q.script, q.params);
      const columns = ['id', 'label', 'salary'];
      const rows = (result.rows || []).map(r =>
        Object.fromEntries(columns.map((c, i) => [c, r[i]]))
      );
      return { view: 'table', kind: 'dsl', data: { columns, rows }, meta: {} };
    },
  },
  {
    queryId: 'impactAnalysis',
    label: '汇报链影响分析',
    meaning: '从陈晓琳出发，沿汇报链追踪',
    dsl: `// Template：影响分析（图）\nawait om.impactAnalysis(db, {\n  rootId: 'emp:alice',\n  relNames: ['reports_to'],\n  maxDepth: 3,\n  direction: 'incoming',\n});`,
    defaultView: 'graph',
    kind: 'template',
    run: async (db) => {
      const result = await om.impactAnalysis(db, {
        rootId: 'emp:alice',
        relNames: ['reports_to'],
        maxDepth: 3,
        direction: 'incoming',
      });
      return { view: 'graph', kind: 'template', data: result.data.visual, meta: result.stats };
    },
  },
  {
    queryId: 'ownershipTree',
    label: '岗位技能树',
    meaning: '陈晓琳 → 岗位 → 技能层级',
    dsl: `// Template：所有权树（树）\nawait om.ownershipTree(db, {\n  rootId: 'emp:alice',\n  ownerRelNames: ['fills_position', 'requires_skill'],\n  maxDepth: 3,\n});`,
    defaultView: 'tree',
    kind: 'template',
    run: async (db) => {
      const result = await om.ownershipTree(db, {
        rootId: 'emp:alice',
        ownerRelNames: ['fills_position', 'requires_skill'],
        maxDepth: 3,
      });
      return { view: 'tree', kind: 'template', data: result.data.visual, meta: result.stats };
    },
  },
  {
    queryId: 'riskHotspot',
    label: '员工薪资风险热点',
    meaning: '按薪资+关联度评估员工风险',
    dsl: `// Template：风险热点（榜单/表格）\nawait om.riskHotspot(db, {\n  typeName: 'Employee',\n  riskAttr: 'salary',\n  topK: 5,\n  minScore: 0,\n  degreeWeight: 10000,\n});`,
    defaultView: 'table',
    kind: 'template',
    run: async (db) => {
      const result = await om.riskHotspot(db, {
        typeName: 'Employee',
        riskAttr: 'salary',
        topK: 5,
        minScore: 0,
        degreeWeight: 10000,
      });
      return { view: 'table', kind: 'template', data: result.data.visual, meta: result.stats };
    },
  },
];

module.exports = {
  demoId: 'hr',
  label: '人力资源',
  defineOntology,
  defaultSheets,
  defaultTables,
  queries,
};
