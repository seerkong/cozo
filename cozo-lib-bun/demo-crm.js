const { CozoDb, om, dsl } = require('.');

// ── 初始化：CRM 本体 ─────────────────────────────────────────────────────────

async function seedOntology(db) {
  await om.initSchema(db);

  // 类型定义
  await om.defineType(db, 'Account', '客户公司 / 账户');
  await om.defineType(db, 'Contact', '客户联系人');
  await om.defineType(db, 'Lead', '销售线索');
  await om.defineType(db, 'Opportunity', '销售商机');
  await om.defineType(db, 'Activity', '跟进活动');
  await om.defineType(db, 'SalesRep', '销售代表');

  // 属性定义
  await om.defineAttribute(db, 'Account', 'industry', 'String', true);
  await om.defineAttribute(db, 'Account', 'annual_revenue', 'Number', false);
  await om.defineAttribute(db, 'Account', 'tier', 'String', false);

  await om.defineAttribute(db, 'Contact', 'email', 'String', true);
  await om.defineAttribute(db, 'Contact', 'phone', 'String', false);
  await om.defineAttribute(db, 'Contact', 'title', 'String', false);

  await om.defineAttribute(db, 'Lead', 'source', 'String', true);
  await om.defineAttribute(db, 'Lead', 'score', 'Number', false);
  await om.defineAttribute(db, 'Lead', 'status', 'String', false);

  await om.defineAttribute(db, 'Opportunity', 'amount', 'Number', true);
  await om.defineAttribute(db, 'Opportunity', 'stage', 'String', true);
  await om.defineAttribute(db, 'Opportunity', 'close_probability', 'Number', false);

  await om.defineAttribute(db, 'Activity', 'activity_type', 'String', true);
  await om.defineAttribute(db, 'Activity', 'due_date', 'String', false);
  await om.defineAttribute(db, 'Activity', 'completed', 'Bool', false);

  await om.defineAttribute(db, 'SalesRep', 'region', 'String', true);
  await om.defineAttribute(db, 'SalesRep', 'quota', 'Number', false);
  await om.defineAttribute(db, 'SalesRep', 'active', 'Bool', false);

  // 关系定义
  await om.defineRelation(db, 'has_contact', 'Account', 'Contact', true);
  await om.defineRelation(db, 'has_opportunity', 'Account', 'Opportunity', true);
  await om.defineRelation(db, 'owned_by', 'Opportunity', 'SalesRep', true);
  await om.defineRelation(db, 'has_activity', 'Opportunity', 'Activity', true);
  await om.defineRelation(db, 'assigned_to', 'Lead', 'SalesRep', true);
  await om.defineRelation(db, 'converts_to', 'Lead', 'Opportunity', true);

  await om.ingestBatch(db, {
    entities: [
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
    properties: [
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
    edges: [
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
  });
}

async function showAnalytics(db) {
  // 遍历：Account -> Opportunity -> Activity
  const acmeToActivities = await om.traverse(db, 'acct:acme', ['has_opportunity', 'has_activity']);
  console.log('--- 遍历 acct:acme -> has_opportunity -> has_activity ---');
  console.log(acmeToActivities);

  // 获取邻居节点
  const repNeighbors = await om.getNeighbors(db, 'rep:li');
  console.log('--- rep:li 的邻居节点 ---');
  console.log(repNeighbors);

  // 按类型查找
  const proposalOpps = await om.findByType(db, 'Opportunity', { stage: 'proposal' });
  console.log('--- stage=proposal 的商机 ---');
  console.log(proposalOpps);

  // 聚合统计
  const totalPipeline = await om.aggregateByType(db, 'Opportunity', 'amount', 'sum');
  const avgLeadScore = await om.aggregateByType(db, 'Lead', 'score', 'avg');
  console.log('--- 聚合结果 ---');
  console.log({ totalPipeline, avgLeadScore });

  // 实体视图
  const oppView = await om.getEntityView(db, 'opp:globex-new');
  console.log('--- 实体视图：opp:globex-new ---');
  console.log(JSON.stringify(oppView, null, 2));

  // DSL v2：高金额商机（amount>60000 且排除 closed_lost）
  const dslOppQuery = dsl
    .query()
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
    .where(
      dsl.and(
        dsl.gt(dsl.var('amount'), dsl.param('min_amount', 60000)),
        dsl.not(dsl.eq(dsl.var('stage'), dsl.param('exclude_stage', 'closed_lost'))),
        dsl.in(dsl.var('id'), [
          dsl.param('o1', 'opp:acme-renew'),
          dsl.param('o2', 'opp:globex-new'),
        ])
      )
    )
    .order('amount')
    .build();

  const dslOppRows = await db.run(dslOppQuery.script, dslOppQuery.params);
  console.log('--- DSL v2：高金额商机查询 ---');
  console.log(dslOppRows.rows);

  // 模板：影响分析
  const impact = await om.impactAnalysis(db, {
    rootId: 'acct:acme',
    relNames: ['has_opportunity', 'owned_by', 'has_activity', 'has_contact'],
    maxDepth: 3,
    direction: 'outgoing',
  });
  console.log('--- 模板：影响分析 ---');
  console.log(impact.stats);
  console.log(impact.data.visual);

  // 模板：所有权树
  const ownership = await om.ownershipTree(db, {
    rootId: 'acct:acme',
    ownerRelNames: ['has_contact', 'has_opportunity', 'owned_by', 'has_activity'],
    maxDepth: 3,
  });
  console.log('--- 模板：所有权树 ---');
  console.log(ownership.stats);
  console.log(ownership.data.visual);

  // 模板：风险热点（amount 作为风险指标）
  const hotspots = await om.riskHotspot(db, {
    typeName: 'Opportunity',
    riskAttr: 'amount',
    topK: 5,
    minScore: 0,
    degreeWeight: 1000,
  });
  console.log('--- 模板：风险热点 ---');
  console.log(hotspots.data.hotspots);
  console.log(hotspots.data.visual);
}

async function showValidationDemos(db) {
  console.log('--- 约束校验演示 ---');

  // 1) 属性类型错误：amount 期望 Number，传入 String
  try {
    await om.setProperty(db, 'opp:acme-renew', 'amount', 'not-a-number');
    console.log('[异常] 类型不匹配校验未能拒绝输入');
  } catch (error) {
    console.log(`[预期] ${error.message}`);
  }

  // 2) 未定义的属性
  try {
    await om.setProperty(db, 'acct:acme', 'credit_limit', 1000000);
    console.log('[异常] 未定义属性校验未能拒绝输入');
  } catch (error) {
    console.log(`[预期] ${error.message}`);
  }

  // 3) 关系端点类型错误：has_contact 期望 Account->Contact，尝试 Contact->Account
  try {
    await om.linkEntities(db, 'ct:alice', 'has_contact', 'acct:acme');
    console.log('[异常] 关系类型校验未能拒绝输入');
  } catch (error) {
    console.log(`[预期] ${error.message}`);
  }

  // 4) 必填属性终态校验失败，然后修复
  await om.createEntity(db, 'opp:draft', 'Opportunity', '草稿商机');
  const draftReport = await om.validateEntity(db, 'opp:draft');
  console.log('--- 草稿商机校验报告 ---');
  console.log(draftReport);

  try {
    await om.finalizeEntity(db, 'opp:draft');
    console.log('[异常] 必填属性校验未能拒绝输入');
  } catch (error) {
    console.log(`[预期] ${error.message}`);
  }

  // 修复：填充必填字段 amount/stage
  await om.setProperty(db, 'opp:draft', 'amount', 50000);
  await om.setProperty(db, 'opp:draft', 'stage', 'qualification');
  await om.finalizeEntity(db, 'opp:draft');
  console.log('[通过] 填充 amount+stage 后必填属性校验通过');
}

async function main() {
  const db = new CozoDb();
  try {
    await seedOntology(db);
    await showAnalytics(db);
    await showValidationDemos(db);
  } finally {
    db.close();
  }
}

main().catch((e) => {
  console.error(e.display || e.message || e);
  process.exitCode = 1;
});
