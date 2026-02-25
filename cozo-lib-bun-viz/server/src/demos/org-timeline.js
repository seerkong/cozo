/**
 * Org timeline demo — bi-temporal org changes.
 */
const { om } = require('cozo-lib-bun');

const demoId = 'org-timeline';
const label = '组织架构时间轴（Temporal）';

// -- Default seed sheets -------------------------------------------------------

const defaultSheets = {
  entities: {
    columns: ['id', 'typeName', 'label'],
    rows: [
      { id: 'dept:eng', typeName: 'Department', label: 'Engineering' },
      { id: 'dept:product', typeName: 'Department', label: 'Product' },
      { id: 'dept:ops', typeName: 'Department', label: 'Operations' },

      { id: 'emp:alice', typeName: 'Employee', label: 'Alice' },
      { id: 'emp:bob', typeName: 'Employee', label: 'Bob' },
      { id: 'emp:carol', typeName: 'Employee', label: 'Carol' },

      { id: 'team:platform', typeName: 'Team', label: 'Platform Team' },
      { id: 'team:growth', typeName: 'Team', label: 'Growth Team' },
    ],
  },
  properties: {
    columns: ['entityId', 'attrName', 'value'],
    rows: [
      { entityId: 'dept:eng', attrName: 'budget', value: 2000000 },
      { entityId: 'dept:product', attrName: 'budget', value: 1200000 },
      { entityId: 'dept:ops', attrName: 'budget', value: 800000 },

      { entityId: 'emp:alice', attrName: 'title', value: 'Engineer' },
      { entityId: 'emp:alice', attrName: 'level', value: 'L4' },
      { entityId: 'emp:bob', attrName: 'title', value: 'Engineer' },
      { entityId: 'emp:bob', attrName: 'level', value: 'L3' },
      { entityId: 'emp:carol', attrName: 'title', value: 'PM' },
      { entityId: 'emp:carol', attrName: 'level', value: 'L4' },

      { entityId: 'team:platform', attrName: 'area', value: 'infra' },
      { entityId: 'team:growth', attrName: 'area', value: 'product' },
    ],
  },
  edges: {
    columns: ['fromId', 'relName', 'toId', 'props'],
    rows: [],
  },
};

const defaultTables = [
  {
    name: '类型定义',
    columns: ['typeName', 'parent_type', 'mixins', 'description'],
    rows: [
      { typeName: 'Department', parent_type: '', mixins: '', description: '组织架构部门' },
      { typeName: 'Employee', parent_type: '', mixins: '', description: '员工' },
      { typeName: 'Team', parent_type: '', mixins: '', description: '团队' },
    ],
  },
  {
    name: '属性定义',
    columns: ['typeName', 'attrName', 'valueType', 'required', 'description'],
    rows: [
      { typeName: 'Department', attrName: 'budget', valueType: 'Number', required: true, description: '部门预算' },

      { typeName: 'Employee', attrName: 'title', valueType: 'String', required: true, description: '岗位' },
      { typeName: 'Employee', attrName: 'level', valueType: 'String', required: true, description: '级别' },

      { typeName: 'Team', attrName: 'area', valueType: 'String', required: false, description: '领域' },
    ],
  },
  {
    name: '关系定义',
    columns: ['relName', 'fromType', 'toType', 'directed', 'description'],
    rows: [
      { relName: 'belongs_to', fromType: 'Employee', toType: 'Department', directed: true, description: '员工所属部门（temporal）' },
      { relName: 'manages', fromType: 'Employee', toType: 'Team', directed: true, description: '员工管理团队（temporal）' },
    ],
  },
  {
    name: '时间轴定义',
    columns: ['model', 'write_when', 'query_api', 'notes'],
    rows: [
      {
        model: 'om_edge(belongs_to/manages) with valid_time: Validity',
        write_when: 'registerBehaviors seeds historical ASSERT/RETRACT edges via linkEntities/unlinkEntities(validTime)',
        query_api: 'om.getNeighborsAsOf / om.getEdgeHistory',
        notes: 'RETRACT uses ~timestamp; as-of queries are powered by CozoDB @ timestamp under the hood',
      },
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

// -- Behavior layer ------------------------------------------------------------

async function registerBehaviors(db) {
  // Seed temporal org changes.
  // ValidTime values are in the past so NOW semantics still show the latest assignment.
  // NOTE: avoid using the same timestamp for RETRACT + ASSERT transitions.
  const t1 = '2024-01-01T00:00:00Z';
  const t2r = '2024-06-30T23:59:59Z';
  const t2 = '2024-07-01T00:00:00Z';
  const t3r = '2025-01-31T23:59:59Z';
  const t3 = '2025-02-01T00:00:00Z';
  const t4r = '2025-05-31T23:59:59Z';
  const t4 = '2025-06-01T00:00:00Z';

  // Alice moves: Eng -> Product -> Ops
  await om.linkEntities(db, 'emp:alice', 'belongs_to', 'dept:eng', { reason: 'join' }, { validTime: t1 });
  await om.unlinkEntities(db, 'emp:alice', 'belongs_to', 'dept:eng', { validTime: t2r });
  await om.linkEntities(db, 'emp:alice', 'belongs_to', 'dept:product', { reason: 'transfer' }, { validTime: t2 });
  await om.unlinkEntities(db, 'emp:alice', 'belongs_to', 'dept:product', { validTime: t3r });
  await om.linkEntities(db, 'emp:alice', 'belongs_to', 'dept:ops', { reason: 'reorg' }, { validTime: t3 });

  // Bob stays in Eng
  await om.linkEntities(db, 'emp:bob', 'belongs_to', 'dept:eng', { reason: 'join' }, { validTime: '2024-03-01T00:00:00Z' });

  // Carol moves: Product -> Eng
  await om.linkEntities(db, 'emp:carol', 'belongs_to', 'dept:product', { reason: 'join' }, { validTime: '2024-05-01T00:00:00Z' });
  await om.unlinkEntities(db, 'emp:carol', 'belongs_to', 'dept:product', { validTime: t4r });
  await om.linkEntities(db, 'emp:carol', 'belongs_to', 'dept:eng', { reason: 'transfer' }, { validTime: t4 });

  // Manager assignments (temporal)
  await om.linkEntities(db, 'emp:alice', 'manages', 'team:platform', { since: t2 }, { validTime: t2 });
  await om.unlinkEntities(db, 'emp:alice', 'manages', 'team:platform', { validTime: t3r });
  await om.linkEntities(db, 'emp:alice', 'manages', 'team:growth', { since: t3 }, { validTime: t3 });
}

// -- Query helpers -------------------------------------------------------------

async function listEmployees(db) {
  return om.findByType(db, 'Employee', {}, { exact: true });
}

async function listDepartments(db) {
  return om.findByType(db, 'Department', {}, { exact: true });
}

async function snapshotAsOf(db, asOfIso) {
  const employees = await listEmployees(db);
  const columns = ['as_of', 'employee_id', 'employee', 'department_id', 'department', 'team_id', 'team'];
  const rows = [];

  for (const emp of employees) {
    const deptNeighbors = await om.getNeighborsAsOf(db, emp.id, 'belongs_to', asOfIso);
    const dept = (deptNeighbors?.outgoing || [])[0] || null;

    const teamNeighbors = await om.getNeighborsAsOf(db, emp.id, 'manages', asOfIso);
    const team = (teamNeighbors?.outgoing || [])[0] || null;

    rows.push({
      as_of: asOfIso,
      employee_id: emp.id,
      employee: emp.label,
      department_id: dept?.entityId ?? null,
      department: dept?.label ?? null,
      team_id: team?.entityId ?? null,
      team: team?.label ?? null,
    });
  }

  rows.sort((a, b) => String(a.employee_id).localeCompare(String(b.employee_id)));
  return { columns, rows };
}

async function deptHeadcountAsOf(db, asOfIso) {
  const depts = await listDepartments(db);
  const columns = ['as_of', 'department_id', 'department', 'headcount'];
  const rows = [];

  for (const dept of depts) {
    const neighbors = await om.getNeighborsAsOf(db, dept.id, 'belongs_to', asOfIso);
    const headcount = Array.isArray(neighbors?.incoming) ? neighbors.incoming.length : 0;
    rows.push({ as_of: asOfIso, department_id: dept.id, department: dept.label, headcount });
  }

  rows.sort((a, b) => String(a.department_id).localeCompare(String(b.department_id)));
  return { columns, rows };
}

async function employeeMoveTimeline(db, employeeId) {
  const hist = await om.getEdgeHistory(db, employeeId, 'belongs_to');
  const columns = ['employee_id', 'valid_time', 'event', 'department_id', 'department_label'];
  const rows = [];

  for (const h of hist) {
    const view = await om.getEntityView(db, h.toId);
    rows.push({
      employee_id: employeeId,
      valid_time: h.valid_time,
      event: h.is_assert ? 'ASSERT' : 'RETRACT',
      department_id: h.toId,
      department_label: view?.label ?? h.toId,
    });
  }

  return { columns, rows };
}

// -- Queries ------------------------------------------------------------------

const queries = [
  {
    queryId: 'snapshot_2024_06',
    label: '快照：2024-06-01',
    meaning: '查看 2024-06-01 时的组织架构（as-of）',
    dsl: "await snapshotAsOf(db, '2024-06-01T00:00:00Z')",
    defaultView: 'table',
    kind: 'template',
    run: async (db) => ({ view: 'table', kind: 'template', data: await snapshotAsOf(db, '2024-06-01T00:00:00Z'), meta: {} }),
  },
  {
    queryId: 'snapshot_2024_12',
    label: '快照：2024-12-01',
    meaning: '查看 2024-12-01 时的组织架构（as-of）',
    dsl: "await snapshotAsOf(db, '2024-12-01T00:00:00Z')",
    defaultView: 'table',
    kind: 'template',
    run: async (db) => ({ view: 'table', kind: 'template', data: await snapshotAsOf(db, '2024-12-01T00:00:00Z'), meta: {} }),
  },
  {
    queryId: 'dept_headcount_2024_12',
    label: '部门人数：2024-12-01',
    meaning: '按部门统计 2024-12-01 的人数（as-of）',
    dsl: "await deptHeadcountAsOf(db, '2024-12-01T00:00:00Z')",
    defaultView: 'table',
    kind: 'template',
    run: async (db) => ({ view: 'table', kind: 'template', data: await deptHeadcountAsOf(db, '2024-12-01T00:00:00Z'), meta: {} }),
  },
  {
    queryId: 'alice_timeline',
    label: '时间轴：Alice 调动',
    meaning: '查看 Alice 的部门调动时间轴（edge history）',
    dsl: "await employeeMoveTimeline(db, 'emp:alice')",
    defaultView: 'table',
    kind: 'template',
    run: async (db) => ({ view: 'table', kind: 'template', data: await employeeMoveTimeline(db, 'emp:alice'), meta: {} }),
  },
];

async function defineOntology(_db) {
  // Not used by the current viz server path; ontology is defined from tables.
}

module.exports = {
  demoId,
  label,
  defineOntology,
  registerBehaviors,
  defaultSheets,
  defaultTables,
  queries,
};
