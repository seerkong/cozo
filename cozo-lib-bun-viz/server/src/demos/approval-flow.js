/**
 * Approval flow demo — Action/Mutation/Interceptor/Constraint/Computed.
 */
const { om } = require('cozo-lib-bun');

// NOTE:
// The viz server defines ontology from `defaultTables` (types/attrs/rels) and ingests data.
// Behavior-layer handlers (actions/mutations/interceptors/constraints/computed) are JS-only,
// so we register them via `registerBehaviors(db)`.

const demoId = 'approval-flow';
const label = '审批流（Action + 约束 + 派生）';

// In this demo we keep:
// - ApprovalRequest.status: operational status (written at ASSERT / "now")
// - ApprovalRequest.effective_status: timeline status (written bi-temporally via valid_time)
// - status_decided_by/status_note: per-timeline metadata aligned with effective_status.valid_time

// -- Default seed sheets -------------------------------------------------------

const defaultSheets = {
  entities: {
    columns: ['id', 'typeName', 'label'],
    rows: [
      { id: 'dept:finance', typeName: 'Department', label: '财务部' },
      { id: 'dept:it', typeName: 'Department', label: '信息技术部' },

      { id: 'appr:alice', typeName: 'Approver', label: 'Alice' },
      { id: 'appr:bob', typeName: 'Approver', label: 'Bob' },
      { id: 'appr:carol', typeName: 'Approver', label: 'Carol' },

      { id: 'req:1001', typeName: 'ApprovalRequest', label: '采购申请 #1001' },
      { id: 'req:1002', typeName: 'ApprovalRequest', label: '采购申请 #1002' },
      { id: 'req:1003', typeName: 'ApprovalRequest', label: '采购申请 #1003' },
      { id: 'req:1004', typeName: 'ApprovalRequest', label: '采购申请 #1004' },
      { id: 'req:1005', typeName: 'ApprovalRequest', label: '采购申请 #1005' },
      { id: 'req:1006', typeName: 'ApprovalRequest', label: '采购申请 #1006' },
    ],
  },
  properties: {
    columns: ['entityId', 'attrName', 'value'],
    rows: [
      { entityId: 'dept:finance', attrName: 'budget_limit', value: 10000 },
      { entityId: 'dept:it', attrName: 'budget_limit', value: 50000 },

      { entityId: 'appr:alice', attrName: 'role', value: 'manager' },
      { entityId: 'appr:bob', attrName: 'role', value: 'director' },
      { entityId: 'appr:carol', attrName: 'role', value: 'cfo' },

      { entityId: 'req:1001', attrName: 'status', value: 'draft' },
      { entityId: 'req:1001', attrName: 'amount', value: 1200 },
      { entityId: 'req:1001', attrName: 'reason', value: '研发设备' },
      { entityId: 'req:1001', attrName: 'requires_review', value: false },
      { entityId: 'req:1001', attrName: 'effective_at', value: '2026-03-01T09:00:00Z' },

      { entityId: 'req:1002', attrName: 'status', value: 'submitted' },
      { entityId: 'req:1002', attrName: 'amount', value: 6000 },
      { entityId: 'req:1002', attrName: 'reason', value: '外包服务' },
      { entityId: 'req:1002', attrName: 'requires_review', value: true },
      { entityId: 'req:1002', attrName: 'effective_at', value: '2026-02-15T09:00:00Z' },

      { entityId: 'req:1003', attrName: 'status', value: 'submitted' },
      { entityId: 'req:1003', attrName: 'amount', value: 15000 },
      { entityId: 'req:1003', attrName: 'reason', value: '年度维护合同' },
      { entityId: 'req:1003', attrName: 'requires_review', value: false },
      { entityId: 'req:1003', attrName: 'effective_at', value: '2026-02-01T09:00:00Z' },

      { entityId: 'req:1004', attrName: 'status', value: 'submitted' },
      { entityId: 'req:1004', attrName: 'amount', value: 9000 },
      { entityId: 'req:1004', attrName: 'reason', value: '培训费用' },
      { entityId: 'req:1004', attrName: 'requires_review', value: true },
      { entityId: 'req:1004', attrName: 'effective_at', value: '2026-02-20T09:00:00Z' },

      { entityId: 'req:1005', attrName: 'status', value: 'approved' },
      { entityId: 'req:1005', attrName: 'amount', value: 3000 },
      { entityId: 'req:1005', attrName: 'reason', value: '差旅报销' },
      { entityId: 'req:1005', attrName: 'requires_review', value: false },
      { entityId: 'req:1005', attrName: 'effective_at', value: '2026-02-10T09:00:00Z' },

      // Keep finance pending (submitted) requests <= 3 to satisfy cross-entity constraint.
      { entityId: 'req:1006', attrName: 'status', value: 'approved' },
      { entityId: 'req:1006', attrName: 'amount', value: 2000 },
      { entityId: 'req:1006', attrName: 'reason', value: '办公用品' },
      { entityId: 'req:1006', attrName: 'requires_review', value: false },
      { entityId: 'req:1006', attrName: 'effective_at', value: '2026-02-12T09:00:00Z' },
    ],
  },
  edges: {
    columns: ['fromId', 'relName', 'toId', 'props'],
    rows: [
      // Approver membership
      { fromId: 'appr:alice', relName: 'member_of', toId: 'dept:it', props: {} },
      { fromId: 'appr:bob', relName: 'member_of', toId: 'dept:it', props: {} },
      { fromId: 'appr:carol', relName: 'member_of', toId: 'dept:finance', props: {} },

      // Request -> Department association (two relations to support different demos)
      { fromId: 'req:1002', relName: 'belongs_to', toId: 'dept:it', props: {} },
      { fromId: 'req:1003', relName: 'belongs_to', toId: 'dept:finance', props: {} },
      { fromId: 'req:1004', relName: 'belongs_to', toId: 'dept:finance', props: {} },
      { fromId: 'req:1005', relName: 'belongs_to', toId: 'dept:finance', props: {} },
      { fromId: 'req:1006', relName: 'belongs_to', toId: 'dept:finance', props: {} },

      // Department -> Requests (used by cross-entity constraint)
      { fromId: 'dept:finance', relName: 'requests', toId: 'req:1003', props: {} },
      { fromId: 'dept:finance', relName: 'requests', toId: 'req:1004', props: {} },
      { fromId: 'dept:finance', relName: 'requests', toId: 'req:1005', props: {} },
      { fromId: 'dept:finance', relName: 'requests', toId: 'req:1006', props: {} },
      { fromId: 'dept:it', relName: 'requests', toId: 'req:1002', props: {} },

      // Assignments
      { fromId: 'req:1002', relName: 'assigned_to', toId: 'appr:alice', props: { step: 1 } },
      { fromId: 'req:1002', relName: 'assigned_to', toId: 'appr:bob', props: { step: 2 } },
      { fromId: 'req:1003', relName: 'assigned_to', toId: 'appr:carol', props: { step: 1 } },
    ],
  },
};

const defaultTables = [
  {
    name: '类型定义',
    columns: ['typeName', 'parent_type', 'mixins', 'description'],
    rows: [
      { typeName: 'ApprovalRequest', parent_type: '', mixins: '', description: '审批申请' },
      { typeName: 'Approver', parent_type: '', mixins: '', description: '审批人' },
      { typeName: 'Department', parent_type: '', mixins: '', description: '部门' },
    ],
  },
  {
    name: '属性定义',
    columns: ['typeName', 'attrName', 'valueType', 'required', 'description'],
    rows: [
      { typeName: 'ApprovalRequest', attrName: 'status', valueType: 'String', required: true, description: '状态' },
      { typeName: 'ApprovalRequest', attrName: 'effective_status', valueType: 'String', required: false, description: '状态时间轴（bi-temporal，valid_time=业务生效时间）' },
      { typeName: 'ApprovalRequest', attrName: 'amount', valueType: 'Number', required: true, description: '金额' },
      { typeName: 'ApprovalRequest', attrName: 'reason', valueType: 'String', required: false, description: '原因' },
      { typeName: 'ApprovalRequest', attrName: 'requires_review', valueType: 'Bool', required: false, description: '是否需要复核' },
      { typeName: 'ApprovalRequest', attrName: 'effective_at', valueType: 'String', required: false, description: '生效时间(RFC3339)' },
      { typeName: 'ApprovalRequest', attrName: 'status_decided_by', valueType: 'String', required: false, description: '状态变更人（与 effective_status 同 valid_time）' },
      { typeName: 'ApprovalRequest', attrName: 'status_note', valueType: 'String', required: false, description: '状态变更备注（与 effective_status 同 valid_time）' },

      { typeName: 'Approver', attrName: 'role', valueType: 'String', required: true, description: '角色' },
      { typeName: 'Department', attrName: 'budget_limit', valueType: 'Number', required: true, description: '预算上限' },
    ],
  },
  {
    name: '关系定义',
    columns: ['relName', 'fromType', 'toType', 'directed', 'description'],
    rows: [
      { relName: 'belongs_to', fromType: 'ApprovalRequest', toType: 'Department', directed: true, description: '所属部门' },
      { relName: 'requests', fromType: 'Department', toType: 'ApprovalRequest', directed: true, description: '部门申请列表' },
      { relName: 'assigned_to', fromType: 'ApprovalRequest', toType: 'Approver', directed: true, description: '分配给' },
      { relName: 'member_of', fromType: 'Approver', toType: 'Department', directed: true, description: '隶属部门' },
    ],
  },

  // ── Behavior-layer definition sheets (for inspection) ────────────────────

  {
    name: 'Action定义',
    columns: ['typeName', 'actionName', 'params', 'mutations', 'description'],
    rows: [
      { typeName: 'ApprovalRequest', actionName: 'submit', params: '{ deptId, approverId, effectiveAt }', mutations: '[setEffectiveAt, linkToDepartment, setStatus(submitted), setEffectiveStatus(submitted@now), recordDecisionTimeline(at=ASSERT), addDeptRequest, assignApprover]', description: '提交审批（携带生效时间）' },
      { typeName: 'ApprovalRequest', actionName: 'approve', params: '{ approverId?, note? }', mutations: '[setStatus(approved), setEffectiveStatus(approved@effective_at), recordDecisionTimeline]', description: '审批通过（状态立即生效 + 时间轴按 effective_at 生效）' },
      { typeName: 'ApprovalRequest', actionName: 'reject', params: '{ approverId?, note? }', mutations: '[setStatus(rejected), setEffectiveStatus(rejected@now), recordDecisionTimeline]', description: '驳回（立即生效）' },
      { typeName: 'ApprovalRequest', actionName: 'escalate', params: '{ approverId }', mutations: '[assignApprover(step=99), setStatus(escalated)]', description: '升级审批' },
    ],
  },
  {
    name: 'Mutation定义',
    columns: ['typeName', 'mutationName', 'inputs', 'effects', 'description'],
    rows: [
      { typeName: 'ApprovalRequest', mutationName: 'setStatus', inputs: '{ status }', effects: 'setProperty(status)', description: '设置审批状态（立即生效）' },
      { typeName: 'ApprovalRequest', mutationName: 'setEffectiveStatus', inputs: '{ status, validTime? }', effects: 'setProperty(effective_status, {validTime})', description: '设置时间轴状态（bi-temporal）' },
      { typeName: 'ApprovalRequest', mutationName: 'setEffectiveAt', inputs: '{ effectiveAt }', effects: 'setProperty(effective_at)', description: '写入生效时间字段' },
      { typeName: 'ApprovalRequest', mutationName: 'setRequiresReview', inputs: '{ value }', effects: 'setProperty(requires_review)', description: '设置 requires_review' },
      { typeName: 'ApprovalRequest', mutationName: 'assignApprover', inputs: '{ approverId, step? }', effects: 'linkEntities(assigned_to)', description: '分配审批人' },
      { typeName: 'ApprovalRequest', mutationName: 'linkToDepartment', inputs: '{ deptId }', effects: 'linkEntities(belongs_to)', description: '关联到部门' },
      { typeName: 'ApprovalRequest', mutationName: 'addDeptRequest', inputs: '{ deptId }', effects: 'linkEntities(dept.requests -> request)', description: '在部门 requests 中登记（触发跨实体约束）' },
      { typeName: 'ApprovalRequest', mutationName: 'recordDecisionTimeline', inputs: '{ decidedBy?, note?, at? }', effects: 'setProperty(status_decided_by/status_note, {validTime: at||effective_at})', description: '写入时间轴元数据（对齐 effective_status.valid_time）' },
    ],
  },
  {
    name: 'Interceptor定义',
    columns: ['typeName', 'actionName', 'phase', 'rule', 'description'],
    rows: [
      { typeName: 'ApprovalRequest', actionName: 'approve', phase: 'before', rule: "status in ['submitted','escalated']", description: '审批前置状态检查' },
      { typeName: 'ApprovalRequest', actionName: 'reject', phase: 'before', rule: "status in ['submitted','escalated']", description: '驳回前置状态检查' },
    ],
  },
  {
    name: 'Constraint定义',
    columns: ['typeName', 'constraintName', 'scope', 'when', 'then', 'message'],
    rows: [
      { typeName: 'ApprovalRequest', constraintName: 'valid_status', scope: 'conditional', when: 'true', then: "status in ['draft','submitted','approved','rejected','escalated']", message: 'status must be one of: draft/submitted/approved/rejected/escalated' },
      { typeName: 'ApprovalRequest', constraintName: 'high_risk_requires_review', scope: 'conditional', when: 'risk_score > 80', then: 'requires_review == true', message: 'requires_review must be true when risk_score > 80' },
      { typeName: 'Department', constraintName: 'max_pending_requests', scope: 'cross-entity', when: 'true', then: "count(requests where status='submitted') <= 3", message: 'department has too many submitted requests (max 3)' },
    ],
  },
  {
    name: 'Computed定义',
    columns: ['typeName', 'attrName', 'logic', 'description'],
    rows: [
      { typeName: 'ApprovalRequest', attrName: 'approval_chain_length', logic: 'count(outgoing assigned_to edges)', description: '审批链长度（assigned_to 出边数量）' },
      { typeName: 'ApprovalRequest', attrName: 'risk_score', logic: 'round(amount / department.budget_limit * 100)', description: '风险评分（金额/预算上限）' },
    ],
  },
  {
    name: '时间轴定义',
    columns: ['model', 'write_when', 'query_api', 'notes'],
    rows: [
      {
        model: 'om_property(effective_status) + aligned metadata properties',
        write_when: 'submit writes effective_status at ASSERT; approve writes effective_status with validTime=effective_at; reject writes effective_status at ASSERT; recordDecisionTimeline aligns metadata by validTime',
        query_api: 'om.getPropertyHistory / om.getPropertyAsOf',
        notes: 'Bi-temporal timeline versions are stored in om_property with valid_time: Validity; as-of uses @ timestamp under the hood',
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
  // Mutations
  await om.defineMutation(
    db,
    'ApprovalRequest',
    'setStatus',
    async (ctx, params) => {
      const status = String(params.status || '').trim();
      if (!status) throw new Error('status is required');
      await ctx.setProperty('status', status);
    },
    '设置审批状态'
  );

  await om.defineMutation(
    db,
    'ApprovalRequest',
    'setEffectiveStatus',
    async (ctx, params) => {
      const status = String(params.status || '').trim();
      if (!status) throw new Error('status is required');
      const vt = params && params.validTime != null ? String(params.validTime).trim() : '';
      await ctx.setProperty('effective_status', status, vt ? { validTime: vt } : undefined);
    },
    '设置时间轴状态（bi-temporal）'
  );

  await om.defineMutation(
    db,
    'ApprovalRequest',
    'setEffectiveAt',
    async (ctx, params) => {
      const effectiveAt = String(params.effectiveAt || '').trim();
      if (!effectiveAt) throw new Error('effectiveAt is required');
      await ctx.setProperty('effective_at', effectiveAt);
    },
    '写入生效时间字段'
  );

  await om.defineMutation(
    db,
    'ApprovalRequest',
    'setRequiresReview',
    async (ctx, params) => {
      await ctx.setProperty('requires_review', !!params.value);
    },
    '设置 requires_review'
  );

  await om.defineMutation(
    db,
    'ApprovalRequest',
    'assignApprover',
    async (ctx, params) => {
      const approverId = String(params.approverId || '').trim();
      if (!approverId) throw new Error('approverId is required');
      await ctx.linkEntities('assigned_to', approverId, { step: params.step ?? 1 });
    },
    '分配审批人'
  );

  await om.defineMutation(
    db,
    'ApprovalRequest',
    'linkToDepartment',
    async (ctx, params) => {
      const deptId = String(params.deptId || '').trim();
      if (!deptId) throw new Error('deptId is required');

      // Link request -> dept (for lookup convenience)
      await ctx.linkEntities('belongs_to', deptId, {});
    },
    '关联到部门'
  );

  await om.defineMutation(
    db,
    'ApprovalRequest',
    'addDeptRequest',
    async (ctx, params) => {
      const deptId = String(params.deptId || '').trim();
      if (!deptId) throw new Error('deptId is required');
      await om.linkEntities(ctx.runner, deptId, 'requests', ctx.entityId, {});
    },
    '在部门 requests 中登记'
  );

  await om.defineMutation(
    db,
    'ApprovalRequest',
    'recordDecisionTimeline',
    async (ctx, params) => {
      const decidedBy = params.decidedBy != null ? String(params.decidedBy) : '';
      const note = params.note != null ? String(params.note) : '';

      const atOverride = params.at != null ? String(params.at).trim() : '';
      const effectiveAt = atOverride || String(await ctx.getProperty('effective_at') || '').trim();
      if (!effectiveAt) {
        throw new Error('effective_at is required (provide effectiveAt when submitting)');
      }

      const vt = String(effectiveAt).trim();
      // Always write marker properties so timeline rows exist even if metadata is empty.
      await ctx.setProperty('status_decided_by', decidedBy, { validTime: vt });
      await ctx.setProperty('status_note', note, { validTime: vt });
    },
    '写入时间轴元数据（对齐 effective_status.valid_time）'
  );

  // Computed properties (lazy, not stored)
  await om.defineComputed(
    db,
    'ApprovalRequest',
    'approval_chain_length',
    async (ctx) => {
      const neighbors = await om.getNeighbors(ctx.runner, ctx.entityId, 'assigned_to');
      return (neighbors?.outgoing || []).length;
    },
    '审批链长度（assigned_to 出边数量）'
  );

  await om.defineComputed(
    db,
    'ApprovalRequest',
    'risk_score',
    async (ctx) => {
      // risk_score = amount / department.budget_limit * 100 (NOW semantics)
      const amount = Number(await ctx.getProperty('amount'));
      if (!Number.isFinite(amount)) return 0;

      const neighbors = await ctx.getNeighbors('belongs_to', 'outgoing');
      const deptId = neighbors?.outgoing?.[0]?.entityId;
      if (!deptId) return 0;

      const budget = Number(await om.getProperty(ctx.runner, deptId, 'budget_limit'));
      if (!Number.isFinite(amount) || !Number.isFinite(budget) || budget <= 0) return 0;
      return Math.round((amount / budget) * 100);
    },
    '风险评分（金额/预算上限）'
  );

  // Constraints
  await om.defineConstraint(db, 'ApprovalRequest', 'valid_status', {
    scope: 'conditional',
    message: 'status must be one of: draft/submitted/approved/rejected/escalated',
    when: async () => true,
    then: async (ctx) => {
      const s = await ctx.getProperty('status');
      return ['draft', 'submitted', 'approved', 'rejected', 'escalated'].includes(String(s));
    },
  });

  await om.defineConstraint(db, 'ApprovalRequest', 'high_risk_requires_review', {
    scope: 'conditional',
    message: 'requires_review must be true when risk_score > 80',
    when: async (ctx) => Number(await ctx.getProperty('risk_score')) > 80,
    then: async (ctx) => (await ctx.getProperty('requires_review')) === true,
  });

  await om.defineConstraint(db, 'Department', 'max_pending_requests', {
    scope: 'cross-entity',
    message: 'department has too many submitted requests (max 3)',
    when: async () => true,
    then: async (ctx) => {
      // Count request edges where request.status == 'submitted' (NOW semantics)
      const neighbors = await ctx.getNeighbors('requests', 'outgoing');
      const requests = Array.isArray(neighbors?.outgoing) ? neighbors.outgoing : [];
      let submittedCount = 0;
      for (const entry of requests) {
        const s = await om.getProperty(ctx.runner, entry.entityId, 'status');
        if (String(s) === 'submitted') {
          submittedCount += 1;
        }
      }
      return submittedCount <= 3;
    },
  });

  // Actions
  await om.defineAction(
    db,
    'ApprovalRequest',
    'submit',
    async (_ctx, params) => {
      const deptId = String(params.deptId || '').trim();
      const approverId = String(params.approverId || '').trim();
      const effectiveAt = String(params.effectiveAt || '').trim();
      if (!deptId) throw new Error('deptId is required');
      if (!approverId) throw new Error('approverId is required');
      if (!effectiveAt) throw new Error('effectiveAt is required');
      return [
        { mutation: 'setEffectiveAt', params: { effectiveAt } },
        { mutation: 'linkToDepartment', params: { deptId } },
        { mutation: 'setStatus', params: { status: 'submitted' } },
        // Timeline: submission is immediate (ASSERT time).
        { mutation: 'setEffectiveStatus', params: { status: 'submitted' } },
        { mutation: 'recordDecisionTimeline', params: { decidedBy: approverId, note: 'submitted', at: 'ASSERT' } },
        // This edge creation triggers Department cross-entity constraint.
        { mutation: 'addDeptRequest', params: { deptId } },
        { mutation: 'assignApprover', params: { approverId, step: 1 } },
      ];
    },
    '提交审批'
  );

  await om.defineAction(
    db,
    'ApprovalRequest',
    'approve',
    async (ctx, params) => {
      const decidedBy = params && params.approverId != null ? String(params.approverId) : '';
      const note = params && params.note != null ? String(params.note) : '';
      const effectiveAt = String(await ctx.getProperty('effective_at') || '').trim();
      if (!effectiveAt) throw new Error('effective_at is required (submit must provide effectiveAt)');
      return [
        // Operational state becomes approved immediately.
        { mutation: 'setStatus', params: { status: 'approved' } },
        // Timeline state becomes approved at business effective time.
        { mutation: 'setEffectiveStatus', params: { status: 'approved', validTime: effectiveAt } },
        { mutation: 'recordDecisionTimeline', params: { decidedBy, note, at: effectiveAt } },
      ];
    },
    '审批通过'
  );

  await om.defineAction(
    db,
    'ApprovalRequest',
    'reject',
    async (_ctx, params) => {
      const decidedBy = params && params.approverId != null ? String(params.approverId) : '';
      const note = params && params.note != null ? String(params.note) : '';
      return [
        { mutation: 'setStatus', params: { status: 'rejected' } },
        // Timeline: reject is immediate (ASSERT time).
        { mutation: 'setEffectiveStatus', params: { status: 'rejected' } },
        { mutation: 'recordDecisionTimeline', params: { decidedBy, note, at: 'ASSERT' } },
      ];
    },
    '驳回'
  );

  await om.defineAction(
    db,
    'ApprovalRequest',
    'escalate',
    async (_ctx, params) => {
      const approverId = String(params.approverId || '').trim();
      if (!approverId) throw new Error('approverId is required');
      return [
        { mutation: 'assignApprover', params: { approverId, step: 99 } },
        { mutation: 'setStatus', params: { status: 'escalated' } },
      ];
    },
    '升级审批'
  );

  // Interceptors
  await om.addInterceptor(db, 'ApprovalRequest', 'approve', 'before', async (ctx) => {
    const status = await ctx.getProperty('status');
    if (String(status) !== 'submitted' && String(status) !== 'escalated') {
      throw new Error('approve requires status=submitted|escalated');
    }
  }, '审批前置状态检查');

  await om.addInterceptor(db, 'ApprovalRequest', 'reject', 'before', async (ctx) => {
    const status = await ctx.getProperty('status');
    if (String(status) !== 'submitted' && String(status) !== 'escalated') {
      throw new Error('reject requires status=submitted|escalated');
    }
  }, '驳回前置状态检查');
}

// -- Query runners ------------------------------------------------------------

function rowsFromEntityViews(views) {
  const columns = ['id', 'label', 'status', 'effective_at', 'amount', 'risk_score', 'requires_review', 'approval_chain_length'];
  const rows = (views || []).map((v) => ({
    id: v.id,
    label: v.label,
    status: v.properties.status,
    effective_at: v.properties.effective_at,
    amount: v.properties.amount,
    risk_score: v.properties.risk_score,
    requires_review: v.properties.requires_review,
    approval_chain_length: v.properties.approval_chain_length,
  }));
  return { columns, rows };
}

async function readTimelineRows(db, requestId) {
  const columns = ['effective_at', 'ts_us', 'is_assert', 'ts_iso', 'status', 'decided_by', 'note'];

  let history = [];
  try {
    history = await om.getPropertyHistory(db, requestId, 'effective_status');
  } catch (_e) {
    history = [];
  }

  const byTimeDecidedBy = new Map();
  const byTimeNote = new Map();
  try {
    const decidedByHist = await om.getPropertyHistory(db, requestId, 'status_decided_by');
    for (const h of decidedByHist) byTimeDecidedBy.set(h.valid_time, h.value);
  } catch (_e) {
    // ignore
  }
  try {
    const noteHist = await om.getPropertyHistory(db, requestId, 'status_note');
    for (const h of noteHist) byTimeNote.set(h.valid_time, h.value);
  } catch (_e) {
    // ignore
  }

  const rows = [];
  for (const h of history) {
    const tsMs = Date.parse(h.valid_time);
    const tsUs = Number.isFinite(tsMs) ? tsMs * 1000 : null;
    rows.push({
      effective_at: h.valid_time,
      ts_us: tsUs,
      is_assert: true,
      ts_iso: h.valid_time,
      status: h.value,
      decided_by: (() => {
        if (!byTimeDecidedBy.has(h.valid_time)) return null;
        const v = byTimeDecidedBy.get(h.valid_time);
        return v == null || String(v) === '' ? null : v;
      })(),
      note: (() => {
        if (!byTimeNote.has(h.valid_time)) return null;
        const v = byTimeNote.get(h.valid_time);
        return v == null || String(v) === '' ? null : v;
      })(),
    });
  }

  // Ensure ascending by effective time.
  rows.sort((a, b) => {
    const ax = typeof a.ts_us === 'number' ? a.ts_us : -1;
    const bx = typeof b.ts_us === 'number' ? b.ts_us : -1;
    return ax - bx;
  });

  return { columns, rows };
}

async function readAsOfStatus(db, requestId, asOfIso) {
  let effectiveStatus;
  try {
    effectiveStatus = await om.getPropertyAsOf(db, requestId, 'effective_status', asOfIso);
  } catch (_e) {
    effectiveStatus = undefined;
  }
  if (effectiveStatus === undefined) return null;

  let decidedBy;
  let note;
  try {
    decidedBy = await om.getPropertyAsOf(db, requestId, 'status_decided_by', asOfIso);
  } catch (_e) {
    decidedBy = undefined;
  }
  try {
    note = await om.getPropertyAsOf(db, requestId, 'status_note', asOfIso);
  } catch (_e) {
    note = undefined;
  }

  let effectiveAt = null;
  try {
    const hist = await om.getPropertyHistory(db, requestId, 'effective_status');
    const asOfMs = Date.parse(String(asOfIso));
    const asOfUs = Number.isFinite(asOfMs) ? asOfMs * 1000 : null;
    if (asOfUs != null) {
      for (const h of hist) {
        const ms = Date.parse(h.valid_time);
        const us = Number.isFinite(ms) ? ms * 1000 : null;
        if (us != null && us <= asOfUs) {
          effectiveAt = h.valid_time;
        }
      }
    }
  } catch (_e) {
    // ignore
  }

  return {
    effective_at: effectiveAt,
    status: effectiveStatus,
    decided_by: decidedBy == null || String(decidedBy) === '' ? null : decidedBy,
    note: note == null || String(note) === '' ? null : note,
  };
}

const queries = [
  {
    queryId: 'approveLowRisk',
    label: '执行 approve（低风险）',
    meaning: '对 req:1002 执行 approve action（应成功）',
    dsl: "await om.executeAction(db, 'req:1002', 'approve', {})",
    defaultView: 'table',
    kind: 'action',
    run: async (db) => {
      await om.executeAction(db, 'req:1002', 'approve', {});
      const view = await om.getEntityView(db, 'req:1002');
      return { view: 'table', kind: 'action', data: rowsFromEntityViews([view]), meta: {} };
    },
  },
  {
    queryId: 'validateFinanceDept',
    label: '校验财务部跨实体约束',
    meaning: '对 dept:finance 执行 validateConstraints（max_pending_requests）',
    dsl: "await om.validateConstraints(db, 'dept:finance')",
    defaultView: 'table',
    kind: 'action',
    run: async (db) => {
      const result = await om.validateConstraints(db, 'dept:finance');
      const columns = ['entityId', 'valid', 'errors'];
      const rows = [{ entityId: 'dept:finance', valid: result.valid, errors: result.errors.join('\n') }];
      return { view: 'table', kind: 'action', data: { columns, rows }, meta: {} };
    },
  },
  {
    queryId: 'timelineAfterApprove',
    label: '时间轴：submit + approve（as-of 对比）',
    meaning: '对 req:1001 执行 submit(携带 future effectiveAt) + approve，然后展示时间轴与 as-of 快照',
    dsl: `// 1) pick a future effectiveAt (relative to now)
 const effectiveAt = new Date(Date.now() + 30 * 24 * 60 * 60 * 1000).toISOString();
 
 // 2) submit with effectiveAt
 await om.executeAction(db, 'req:1001', 'submit', {
   deptId: 'dept:it',
   approverId: 'appr:alice',
   effectiveAt,
 });
 
 // 3) approve (writes effective_status at effectiveAt)
 await om.executeAction(db, 'req:1001', 'approve', { approverId: 'appr:alice', note: 'ok' });
 
 // 4) query timeline
 // use om.getPropertyHistory('effective_status')
 // query as-of before/after effectiveAt`,
    defaultView: 'table',
    kind: 'action',
    run: async (db) => {
      const effectiveAt = new Date(Date.now() + 30 * 24 * 60 * 60 * 1000).toISOString();
      await om.executeAction(db, 'req:1001', 'submit', {
        deptId: 'dept:it',
        approverId: 'appr:alice',
        effectiveAt,
      });
      await om.executeAction(db, 'req:1001', 'approve', { approverId: 'appr:alice', note: 'ok' });

      // Return a unified table: timeline rows + two as-of snapshot rows.
      const timeline = await readTimelineRows(db, 'req:1001');

      const asOfBeforeIso = new Date(Date.now() + 7 * 24 * 60 * 60 * 1000).toISOString();
      const asOfAfterIso = new Date(Date.now() + 90 * 24 * 60 * 60 * 1000).toISOString();
      const asOfBefore = await readAsOfStatus(db, 'req:1001', asOfBeforeIso);
      const asOfAfter = await readAsOfStatus(db, 'req:1001', asOfAfterIso);

      const columns = ['kind', ...timeline.columns];
      const rows = [];
      for (const r of timeline.rows) {
        rows.push({ kind: 'timeline', ...r });
      }
      rows.push({
        kind: `as_of:${asOfBeforeIso}`,
        effective_at: asOfBefore?.effective_at ?? null,
        ts_us: null,
        is_assert: null,
        ts_iso: null,
        status: asOfBefore?.status ?? null,
        decided_by: asOfBefore?.decided_by ?? null,
        note: asOfBefore?.note ?? null,
      });
      rows.push({
        kind: `as_of:${asOfAfterIso}`,
        effective_at: asOfAfter?.effective_at ?? null,
        ts_us: null,
        is_assert: null,
        ts_iso: null,
        status: asOfAfter?.status ?? null,
        decided_by: asOfAfter?.decided_by ?? null,
        note: asOfAfter?.note ?? null,
      });
      return { view: 'table', kind: 'action', data: { columns, rows }, meta: {} };
    },
  },
  {
    queryId: 'requestsWithComputed',
    label: '审批申请（含派生属性）',
    meaning: '列出所有 ApprovalRequest，并展示 risk_score / approval_chain_length',
    dsl: "for (const e of await om.findByType(db, 'ApprovalRequest', {}, { exact: true })) await om.getEntityView(db, e.id)",
    defaultView: 'table',
    kind: 'template',
    run: async (db) => {
      const entries = await om.findByType(db, 'ApprovalRequest', {}, { exact: true });
      const views = [];
      for (const e of entries) {
        const view = await om.getEntityView(db, e.id);
        if (view) views.push(view);
      }
      return { view: 'table', kind: 'template', data: rowsFromEntityViews(views), meta: {} };
    },
  },
  {
    queryId: 'submitDraft',
    label: '执行 submit（含跨实体约束）',
    meaning: "对 req:1001 执行 submit，写入关系并触发 Department 跨实体约束",
    dsl: "await om.executeAction(db, 'req:1001', 'submit', { deptId: 'dept:it', approverId: 'appr:alice', effectiveAt: new Date(Date.now() + 30*24*60*60*1000).toISOString() })",
    defaultView: 'table',
    kind: 'action',
    run: async (db) => {
      await om.executeAction(db, 'req:1001', 'submit', { deptId: 'dept:it', approverId: 'appr:alice', effectiveAt: new Date(Date.now() + 30*24*60*60*1000).toISOString() });
      const view = await om.getEntityView(db, 'req:1001');
      return { view: 'table', kind: 'action', data: rowsFromEntityViews([view]), meta: {} };
    },
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
