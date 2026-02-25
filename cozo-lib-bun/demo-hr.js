const { CozoDb, om, dsl } = require('.');

// ─── 本体定义与种子数据 ─────────────────────────────────────────────────────

async function seedOntology(db) {
  await om.initSchema(db);

  // --- 类型 ---
  await om.defineType(db, 'Employee', '组织中的在职员工');
  await om.defineType(db, 'Department', '组织架构单元');
  await om.defineType(db, 'Position', '部门内的岗位角色');
  await om.defineType(db, 'Skill', '技术能力或软技能');
  await om.defineType(db, 'ReviewCycle', '周期性绩效考核');

  // --- 属性 ---
  await om.defineAttribute(db, 'Employee', 'email', 'String', true);
  await om.defineAttribute(db, 'Employee', 'salary', 'Number', true);
  await om.defineAttribute(db, 'Employee', 'performance_score', 'Number', false);
  await om.defineAttribute(db, 'Employee', 'competency_json', 'Json', false);

  await om.defineAttribute(db, 'Department', 'budget', 'Number', true);
  await om.defineAttribute(db, 'Department', 'headcount', 'Number', true);
  await om.defineAttribute(db, 'Department', 'location', 'String', false);

  await om.defineAttribute(db, 'Position', 'level', 'String', true);
  await om.defineAttribute(db, 'Position', 'min_salary', 'Number', false);
  await om.defineAttribute(db, 'Position', 'max_salary', 'Number', false);

  await om.defineAttribute(db, 'Skill', 'category', 'String', true);

  await om.defineAttribute(db, 'ReviewCycle', 'quarter', 'String', true);
  await om.defineAttribute(db, 'ReviewCycle', 'year', 'Number', true);
  await om.defineAttribute(db, 'ReviewCycle', 'status', 'String', false);

  // --- 关系 ---
  await om.defineRelation(db, 'works_in', 'Employee', 'Department', true);
  await om.defineRelation(db, 'reports_to', 'Employee', 'Employee', true);
  await om.defineRelation(db, 'fills_position', 'Employee', 'Position', true);
  await om.defineRelation(db, 'requires_skill', 'Position', 'Skill', true);
  await om.defineRelation(db, 'reviewed_in', 'Employee', 'ReviewCycle', true);

  // --- 批量导入 ---
  await om.ingestBatch(db, {
    entities: [
      // 员工 (7)
      { id: 'emp:alice', typeName: 'Employee', label: '陈晓琳' },
      { id: 'emp:bob', typeName: 'Employee', label: '马志远' },
      { id: 'emp:carol', typeName: 'Employee', label: '王思雨' },
      { id: 'emp:dave', typeName: 'Employee', label: '金大伟' },
      { id: 'emp:eve', typeName: 'Employee', label: '周怡然' },
      { id: 'emp:frank', typeName: 'Employee', label: '李峰' },
      { id: 'emp:grace', typeName: 'Employee', label: '朴恩惠' },
      // 部门 (2)
      { id: 'dept:eng', typeName: 'Department', label: '工程部' },
      { id: 'dept:product', typeName: 'Department', label: '产品部' },
      // 岗位 (3)
      { id: 'pos:swe', typeName: 'Position', label: '软件工程师' },
      { id: 'pos:lead', typeName: 'Position', label: '技术负责人' },
      { id: 'pos:pm', typeName: 'Position', label: '产品经理' },
      // 技能 (2)
      { id: 'sk:js', typeName: 'Skill', label: 'JavaScript' },
      { id: 'sk:sys', typeName: 'Skill', label: '系统设计' },
      // 考核周期 (2)
      { id: 'rc:q1', typeName: 'ReviewCycle', label: '2026年第一季度考核' },
      { id: 'rc:q2', typeName: 'ReviewCycle', label: '2026年第二季度考核' },
    ],
    properties: [
      // 员工
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
      // 部门
      { entityId: 'dept:eng', attrName: 'budget', value: 2000000 },
      { entityId: 'dept:eng', attrName: 'headcount', value: 5 },
      { entityId: 'dept:eng', attrName: 'location', value: '北京' },
      { entityId: 'dept:product', attrName: 'budget', value: 800000 },
      { entityId: 'dept:product', attrName: 'headcount', value: 2 },
      { entityId: 'dept:product', attrName: 'location', value: '上海' },
      // 岗位
      { entityId: 'pos:swe', attrName: 'level', value: 'IC3' },
      { entityId: 'pos:swe', attrName: 'min_salary', value: 120000 },
      { entityId: 'pos:swe', attrName: 'max_salary', value: 160000 },
      { entityId: 'pos:lead', attrName: 'level', value: 'IC5' },
      { entityId: 'pos:lead', attrName: 'min_salary', value: 160000 },
      { entityId: 'pos:lead', attrName: 'max_salary', value: 200000 },
      { entityId: 'pos:pm', attrName: 'level', value: 'IC4' },
      { entityId: 'pos:pm', attrName: 'min_salary', value: 140000 },
      { entityId: 'pos:pm', attrName: 'max_salary', value: 180000 },
      // 技能
      { entityId: 'sk:js', attrName: 'category', value: '编程语言' },
      { entityId: 'sk:sys', attrName: 'category', value: '架构设计' },
      // 考核周期
      { entityId: 'rc:q1', attrName: 'quarter', value: 'Q1' },
      { entityId: 'rc:q1', attrName: 'year', value: 2026 },
      { entityId: 'rc:q1', attrName: 'status', value: 'completed' },
      { entityId: 'rc:q2', attrName: 'quarter', value: 'Q2' },
      { entityId: 'rc:q2', attrName: 'year', value: 2026 },
      { entityId: 'rc:q2', attrName: 'status', value: 'in_progress' },
    ],
    edges: [
      // works_in: 员工 -> 部门
      { fromId: 'emp:alice', relName: 'works_in', toId: 'dept:eng', props: { since: '2022-03-01' } },
      { fromId: 'emp:bob', relName: 'works_in', toId: 'dept:eng', props: { since: '2023-06-15' } },
      { fromId: 'emp:carol', relName: 'works_in', toId: 'dept:eng', props: { since: '2023-09-01' } },
      { fromId: 'emp:dave', relName: 'works_in', toId: 'dept:eng', props: { since: '2024-01-10' } },
      { fromId: 'emp:frank', relName: 'works_in', toId: 'dept:eng', props: { since: '2024-07-01' } },
      { fromId: 'emp:eve', relName: 'works_in', toId: 'dept:product', props: { since: '2022-11-01' } },
      { fromId: 'emp:grace', relName: 'works_in', toId: 'dept:product', props: { since: '2023-04-20' } },
      // reports_to: 员工 -> 员工（汇报链）
      { fromId: 'emp:bob', relName: 'reports_to', toId: 'emp:alice', props: {} },
      { fromId: 'emp:carol', relName: 'reports_to', toId: 'emp:alice', props: {} },
      { fromId: 'emp:dave', relName: 'reports_to', toId: 'emp:bob', props: {} },
      { fromId: 'emp:frank', relName: 'reports_to', toId: 'emp:bob', props: {} },
      { fromId: 'emp:grace', relName: 'reports_to', toId: 'emp:eve', props: {} },
      // fills_position: 员工 -> 岗位
      { fromId: 'emp:alice', relName: 'fills_position', toId: 'pos:lead', props: { start: '2022-03-01' } },
      { fromId: 'emp:bob', relName: 'fills_position', toId: 'pos:lead', props: { start: '2023-06-15' } },
      { fromId: 'emp:carol', relName: 'fills_position', toId: 'pos:swe', props: { start: '2023-09-01' } },
      { fromId: 'emp:dave', relName: 'fills_position', toId: 'pos:swe', props: { start: '2024-01-10' } },
      { fromId: 'emp:eve', relName: 'fills_position', toId: 'pos:pm', props: { start: '2022-11-01' } },
      // requires_skill: 岗位 -> 技能
      { fromId: 'pos:swe', relName: 'requires_skill', toId: 'sk:js', props: { proficiency: 'advanced' } },
      { fromId: 'pos:lead', relName: 'requires_skill', toId: 'sk:js', props: { proficiency: 'expert' } },
      { fromId: 'pos:lead', relName: 'requires_skill', toId: 'sk:sys', props: { proficiency: 'advanced' } },
      { fromId: 'pos:pm', relName: 'requires_skill', toId: 'sk:sys', props: { proficiency: 'intermediate' } },
      // reviewed_in: 员工 -> 考核周期
      { fromId: 'emp:alice', relName: 'reviewed_in', toId: 'rc:q1', props: { rating: 4.5 } },
      { fromId: 'emp:bob', relName: 'reviewed_in', toId: 'rc:q1', props: { rating: 4.2 } },
      { fromId: 'emp:carol', relName: 'reviewed_in', toId: 'rc:q1', props: { rating: 3.8 } },
      { fromId: 'emp:eve', relName: 'reviewed_in', toId: 'rc:q1', props: { rating: 4.0 } },
    ],
  });
}

// ─── 数据分析 ────────────────────────────────────────────────────────────────

async function showAnalytics(db) {
  // 1. 遍历汇报链：金大伟 -> 马志远 -> 陈晓琳（reports_to 路径）
  const daveManagerChain = await om.traverse(db, 'emp:dave', ['reports_to']);
  console.log('--- 金大伟的汇报链（reports_to）---');
  console.log(daveManagerChain);

  // 2. 遍历组织路径：陈晓琳 -> 岗位 -> 所需技能
  //    通过 fills_position + requires_skill 查找陈晓琳岗位所需的技能
  const aliceSkillPath = await om.traverse(db, 'emp:alice', ['fills_position', 'requires_skill']);
  console.log('--- 陈晓琳岗位所需技能 ---');
  console.log(aliceSkillPath);

  // 3. getNeighbors：马志远的所有邻居节点
  const bobNeighbors = await om.getNeighbors(db, 'emp:bob');
  console.log('--- 马志远的所有邻居节点 ---');
  console.log(JSON.stringify(bobNeighbors, null, 2));

  // 4. findByType：按薪资筛选员工
  const highEarners = await om.findByType(db, 'Employee', { salary: 180000 });
  console.log('--- 薪资=180000的员工 ---');
  console.log(highEarners);

  // 5. aggregateByType：薪资统计
  const totalSalary = await om.aggregateByType(db, 'Employee', 'salary', 'sum');
  const avgSalary = await om.aggregateByType(db, 'Employee', 'salary', 'avg');
  const maxSalary = await om.aggregateByType(db, 'Employee', 'salary', 'max');
  const minSalary = await om.aggregateByType(db, 'Employee', 'salary', 'min');
  console.log('--- 员工薪资汇总 ---');
  console.log({ totalSalary, avgSalary, maxSalary, minSalary });

  // 6. aggregateByType：部门预算
  const totalBudget = await om.aggregateByType(db, 'Department', 'budget', 'sum');
  console.log('--- 部门预算合计 ---');
  console.log({ totalBudget });

  // 7. getEntityView：陈晓琳的详细视图
  const aliceView = await om.getEntityView(db, 'emp:alice');
  console.log('--- 实体视图：陈晓琳 ---');
  console.log(JSON.stringify(aliceView, null, 2));

  // 8. DSL 查询，使用 dsl.not(...) 和 v2 条件：
  //    查找薪资 > 130000 且 NOT 薪资 < 140000 的员工（即薪资 >= 140000）
  const dslSalaryQuery = dsl
    .query()
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
    .where(
      dsl.and(
        dsl.gt(dsl.var('salary'), dsl.param('min_sal', 130000)),
        dsl.not(dsl.lt(dsl.var('salary'), dsl.param('cutoff', 140000))),
        dsl.in(dsl.var('id'), [
          dsl.param('e1', 'emp:alice'),
          dsl.param('e2', 'emp:bob'),
          dsl.param('e3', 'emp:carol'),
          dsl.param('e4', 'emp:dave'),
          dsl.param('e5', 'emp:eve'),
          dsl.param('e6', 'emp:frank'),
          dsl.param('e7', 'emp:grace'),
        ])
      )
    )
    .order('salary')
    .build();

  const dslSalaryRows = await db.run(dslSalaryQuery.script, dslSalaryQuery.params);
  console.log('--- DSL v2：薪资>=140000的员工（使用 not+and+in）---');
  console.log(dslSalaryRows.rows);

  // 9. 模板：impactAnalysis —— 从陈晓琳出发，沿 reports_to（incoming = 谁向她汇报）
  const impact = await om.impactAnalysis(db, {
    rootId: 'emp:alice',
    relNames: ['reports_to'],
    maxDepth: 3,
    direction: 'incoming',
  });
  console.log('--- 模板：impactAnalysis（陈晓琳的汇报链）---');
  console.log(impact.stats);
  console.log(JSON.stringify(impact.data.visual.graph.adjacency, null, 2));

  // 10. 模板：ownershipTree —— 陈晓琳通过 fills_position（查看岗位技能树）
  const ownership = await om.ownershipTree(db, {
    rootId: 'emp:alice',
    ownerRelNames: ['fills_position', 'requires_skill'],
    maxDepth: 3,
  });
  console.log('--- 模板：ownershipTree（陈晓琳 -> 岗位 -> 技能）---');
  console.log(ownership.stats);
  console.log(JSON.stringify(ownership.data.visual.tree, null, 2));

  // 11. 模板：riskHotspot —— 按薪资评估员工风险（高薪资 + 高关联度 = 风险）
  const hotspots = await om.riskHotspot(db, {
    typeName: 'Employee',
    riskAttr: 'salary',
    topK: 5,
    minScore: 0,
    degreeWeight: 10000,
  });
  console.log('--- 模板：riskHotspot（按薪资+关联度评估员工风险）---');
  console.log(hotspots.data.hotspots);
  console.log(hotspots.data.visual);
}

// ─── 校验演示 ────────────────────────────────────────────────────────────────

async function showValidationDemos(db) {
  console.log('\n--- 约束校验演示 ---');

  // 1. 属性类型错误：salary 期望 Number，传入 String
  try {
    await om.setProperty(db, 'emp:alice', 'salary', 'not-a-number');
    console.log('[异常] 类型校验未能拒绝输入');
  } catch (error) {
    console.log(`[预期] 类型不匹配：${error.message}`);
  }

  // 2. 未定义属性：Employee 上未定义 'nickname'
  try {
    await om.setProperty(db, 'emp:alice', 'nickname', 'Ali');
    console.log('[异常] 未定义属性校验未能拒绝输入');
  } catch (error) {
    console.log(`[预期] 未定义属性：${error.message}`);
  }

  // 3. 关系端点错误：works_in 期望 Employee->Department，尝试 Skill->Department
  try {
    await om.linkEntities(db, 'sk:js', 'works_in', 'dept:eng');
    console.log('[异常] 关系端点校验未能拒绝输入');
  } catch (error) {
    console.log(`[预期] 端点错误：${error.message}`);
  }

  // 4. 未定义关系
  try {
    await om.linkEntities(db, 'emp:alice', 'mentors', 'emp:bob');
    console.log('[异常] 未定义关系校验未能拒绝输入');
  } catch (error) {
    console.log(`[预期] 未定义关系：${error.message}`);
  }

  // 5. finalizeEntity 必填字段缺失，然后修复
  await om.createEntity(db, 'emp:temp', 'Employee', '临时员工');
  const tempValidation = await om.validateEntity(db, 'emp:temp');
  console.log('--- 临时员工校验（缺少必填字段）---');
  console.log(tempValidation);

  try {
    await om.finalizeEntity(db, 'emp:temp');
    console.log('[异常] 必填属性校验未能拒绝输入');
  } catch (error) {
    console.log(`[预期] 定稿失败：${error.message}`);
  }

  // 修复：填写必填字段（email、salary）
  await om.setProperty(db, 'emp:temp', 'email', 'temp@acme.com');
  await om.setProperty(db, 'emp:temp', 'salary', 100000);
  await om.finalizeEntity(db, 'emp:temp');
  console.log('[成功] 临时员工在补充 email + salary 后定稿完成');
}

// ─── 主程序 ──────────────────────────────────────────────────────────────────

async function main() {
  const db = new CozoDb();
  try {
    await seedOntology(db);
    console.log('=== 数据初始化完成 ===\n');
    await showAnalytics(db);
    await showValidationDemos(db);
    console.log('\n=== 演示完成 ===');
  } finally {
    db.close();
  }
}

main().catch((e) => {
  console.error(e.display || e.message || e);
  process.exitCode = 1;
});
