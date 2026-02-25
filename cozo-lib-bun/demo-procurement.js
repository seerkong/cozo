const { CozoDb, om, dsl } = require('.');

async function seedOntology(db) {
  await om.initSchema(db);

  // --- 定义类型 ---
  await om.defineType(db, 'Supplier', '提供商品或服务的供应商');
  await om.defineType(db, 'PurchaseOrder', '向供应商下达的采购订单');
  await om.defineType(db, 'LineItem', '采购订单中的单个行项目');
  await om.defineType(db, 'Warehouse', '货物的实体仓储地点');
  await om.defineType(db, 'Contract', '管理采购条款的法律协议');

  // --- 定义属性（必填 + 数字/字符串/JSON 混合） ---
  await om.defineAttribute(db, 'Supplier', 'rating', 'Number', true);
  await om.defineAttribute(db, 'Supplier', 'country', 'String', true);
  await om.defineAttribute(db, 'Supplier', 'contact_email', 'String', false);

  await om.defineAttribute(db, 'PurchaseOrder', 'total_amount', 'Number', true);
  await om.defineAttribute(db, 'PurchaseOrder', 'status', 'String', true);
  await om.defineAttribute(db, 'PurchaseOrder', 'order_date', 'String', false);

  await om.defineAttribute(db, 'LineItem', 'unit_price', 'Number', true);
  await om.defineAttribute(db, 'LineItem', 'quantity', 'Number', true);
  await om.defineAttribute(db, 'LineItem', 'sku', 'String', false);

  await om.defineAttribute(db, 'Warehouse', 'capacity', 'Number', true);
  await om.defineAttribute(db, 'Warehouse', 'location', 'String', true);

  await om.defineAttribute(db, 'Contract', 'risk_score', 'Number', true);
  await om.defineAttribute(db, 'Contract', 'terms_json', 'Json', true);
  await om.defineAttribute(db, 'Contract', 'valid_until', 'String', false);

  // --- 定义关系 ---
  await om.defineRelation(db, 'placed_with', 'PurchaseOrder', 'Supplier', true);
  await om.defineRelation(db, 'has_line_item', 'PurchaseOrder', 'LineItem', true);
  await om.defineRelation(db, 'fulfilled_by', 'LineItem', 'Warehouse', true);
  await om.defineRelation(db, 'covered_by', 'PurchaseOrder', 'Contract', true);
  await om.defineRelation(db, 'supplies', 'Supplier', 'Warehouse', true);

  // --- 通过 ingestBatch 导入种子数据 ---
  await om.ingestBatch(db, {
    entities: [
      // 供应商 (3)
      { id: 's:acme', typeName: 'Supplier', label: '先达公司' },
      { id: 's:globex', typeName: 'Supplier', label: '环宇工业' },
      { id: 's:initech', typeName: 'Supplier', label: '启泰有限公司' },
      // 采购订单 (3)
      { id: 'po:1001', typeName: 'PurchaseOrder', label: '采购单-1001' },
      { id: 'po:1002', typeName: 'PurchaseOrder', label: '采购单-1002' },
      { id: 'po:1003', typeName: 'PurchaseOrder', label: '采购单-1003' },
      // 行项目 (4)
      { id: 'li:a', typeName: 'LineItem', label: '钢梁 x200' },
      { id: 'li:b', typeName: 'LineItem', label: '铜线 x500' },
      { id: 'li:c', typeName: 'LineItem', label: '电路板 x1000' },
      { id: 'li:d', typeName: 'LineItem', label: '橡胶垫圈 x300' },
      // 仓库 (2)
      { id: 'wh:east', typeName: 'Warehouse', label: '华东仓储中心' },
      { id: 'wh:west', typeName: 'Warehouse', label: '华西仓储中心' },
      // 合同 (2) — 共 14 个实体
      { id: 'ct:master', typeName: 'Contract', label: '主供应协议' },
      { id: 'ct:spot', typeName: 'Contract', label: '现货采购合同' },
    ],
    properties: [
      // 供应商属性 (7)
      { entityId: 's:acme', attrName: 'rating', value: 4.5 },
      { entityId: 's:acme', attrName: 'country', value: 'US' },
      { entityId: 's:acme', attrName: 'contact_email', value: 'sales@acme.example' },
      { entityId: 's:globex', attrName: 'rating', value: 3.8 },
      { entityId: 's:globex', attrName: 'country', value: 'DE' },
      { entityId: 's:initech', attrName: 'rating', value: 2.1 },
      { entityId: 's:initech', attrName: 'country', value: 'CN' },
      // 采购订单属性 (6)
      { entityId: 'po:1001', attrName: 'total_amount', value: 54000 },
      { entityId: 'po:1001', attrName: 'status', value: 'approved' },
      { entityId: 'po:1001', attrName: 'order_date', value: '2026-01-15' },
      { entityId: 'po:1002', attrName: 'total_amount', value: 12750 },
      { entityId: 'po:1002', attrName: 'status', value: 'pending' },
      { entityId: 'po:1003', attrName: 'total_amount', value: 87200 },
      { entityId: 'po:1003', attrName: 'status', value: 'approved' },
      // 行项目属性 (8)
      { entityId: 'li:a', attrName: 'unit_price', value: 270 },
      { entityId: 'li:a', attrName: 'quantity', value: 200 },
      { entityId: 'li:b', attrName: 'unit_price', value: 25.5 },
      { entityId: 'li:b', attrName: 'quantity', value: 500 },
      { entityId: 'li:c', attrName: 'unit_price', value: 87.2 },
      { entityId: 'li:c', attrName: 'quantity', value: 1000 },
      { entityId: 'li:d', attrName: 'unit_price', value: 4.25 },
      { entityId: 'li:d', attrName: 'quantity', value: 300 },
      // 仓库属性 (4)
      { entityId: 'wh:east', attrName: 'capacity', value: 50000 },
      { entityId: 'wh:east', attrName: 'location', value: '江苏南京' },
      { entityId: 'wh:west', attrName: 'capacity', value: 35000 },
      { entityId: 'wh:west', attrName: 'location', value: '四川成都' },
      // 合同属性 (4)
      { entityId: 'ct:master', attrName: 'risk_score', value: 15 },
      { entityId: 'ct:master', attrName: 'terms_json', value: { duration_months: 24, penalty_pct: 5, auto_renew: true } },
      { entityId: 'ct:master', attrName: 'valid_until', value: '2028-01-01' },
      { entityId: 'ct:spot', attrName: 'risk_score', value: 42 },
      { entityId: 'ct:spot', attrName: 'terms_json', value: { duration_months: 3, penalty_pct: 0, auto_renew: false } },
    ],
    edges: [
      // placed_with: 采购订单 -> 供应商 (3)
      { fromId: 'po:1001', relName: 'placed_with', toId: 's:acme', props: { buyer: '采购一组' } },
      { fromId: 'po:1002', relName: 'placed_with', toId: 's:globex', props: { buyer: '采购二组' } },
      { fromId: 'po:1003', relName: 'placed_with', toId: 's:initech', props: { buyer: '采购一组' } },
      // has_line_item: 采购订单 -> 行项目 (4)
      { fromId: 'po:1001', relName: 'has_line_item', toId: 'li:a', props: { seq: 1 } },
      { fromId: 'po:1001', relName: 'has_line_item', toId: 'li:b', props: { seq: 2 } },
      { fromId: 'po:1002', relName: 'has_line_item', toId: 'li:c', props: { seq: 1 } },
      { fromId: 'po:1003', relName: 'has_line_item', toId: 'li:d', props: { seq: 1 } },
      // fulfilled_by: 行项目 -> 仓库 (4)
      { fromId: 'li:a', relName: 'fulfilled_by', toId: 'wh:east', props: { eta_days: 5 } },
      { fromId: 'li:b', relName: 'fulfilled_by', toId: 'wh:west', props: { eta_days: 3 } },
      { fromId: 'li:c', relName: 'fulfilled_by', toId: 'wh:east', props: { eta_days: 7 } },
      { fromId: 'li:d', relName: 'fulfilled_by', toId: 'wh:west', props: { eta_days: 2 } },
      // covered_by: 采购订单 -> 合同 (2)
      { fromId: 'po:1001', relName: 'covered_by', toId: 'ct:master', props: { clause: '第四条甲款' } },
      { fromId: 'po:1003', relName: 'covered_by', toId: 'ct:spot', props: { clause: '附录乙' } },
      // supplies: 供应商 -> 仓库 (3)
      { fromId: 's:acme', relName: 'supplies', toId: 'wh:east', props: { since: '2024-06-01' } },
      { fromId: 's:globex', relName: 'supplies', toId: 'wh:west', props: { since: '2025-01-15' } },
      { fromId: 's:initech', relName: 'supplies', toId: 'wh:west', props: { since: '2025-09-01' } },
    ],
  });
}

async function showAnalytics(db) {
  // 多跳遍历: PO-1001 -> has_line_item -> fulfilled_by（到达仓库）
  const poToWarehouses = await om.traverse(db, 'po:1001', ['has_line_item', 'fulfilled_by']);
  console.log('--- 多跳遍历: PO-1001 -> 行项目 -> 仓库 ---');
  console.log(poToWarehouses);

  // 获取供应商的邻居节点
  const acmeNeighbors = await om.getNeighbors(db, 's:acme');
  console.log('--- 先达公司的邻居节点 ---');
  console.log(acmeNeighbors);

  // 按类型筛选
  const approvedPOs = await om.findByType(db, 'PurchaseOrder', { status: 'approved' });
  console.log('--- 已审批的采购订单 ---');
  console.log(approvedPOs);

  // 聚合查询
  const totalSpend = await om.aggregateByType(db, 'PurchaseOrder', 'total_amount', 'sum');
  const avgUnitPrice = await om.aggregateByType(db, 'LineItem', 'unit_price', 'avg');
  const maxRisk = await om.aggregateByType(db, 'Contract', 'risk_score', 'max');
  console.log('--- 聚合统计 ---');
  console.log({ totalSpend, avgUnitPrice, maxRisk });

  // 实体视图
  const po1001View = await om.getEntityView(db, 'po:1001');
  console.log('--- 实体视图: PO-1001 ---');
  console.log(JSON.stringify(po1001View, null, 2));

  // 自定义 DSL 查询: 查找单价 > 20 且数量 >= 200 的行项目
  const dslLineItems = dsl
    .query()
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
    .where(
      dsl.and(
        dsl.gt(dsl.var('price'), dsl.param('min_price', 20)),
        dsl.gte(dsl.var('qty'), dsl.param('min_qty', 200)),
        dsl.not(dsl.eq(dsl.var('price'), dsl.param('exclude_price', 0))),
        dsl.in(dsl.var('id'), [
          dsl.param('li_a', 'li:a'),
          dsl.param('li_b', 'li:b'),
          dsl.param('li_c', 'li:c'),
          dsl.param('li_d', 'li:d'),
        ])
      )
    )
    .order('price')
    .build();

  const dslRows = await db.run(dslLineItems.script, dslLineItems.params);
  console.log('--- DSL 查询: 高价值行项目 ---');
  console.log(dslRows.rows);

  // 模板: impactAnalysis — 从 PO-1001 向外追踪
  const impact = await om.impactAnalysis(db, {
    rootId: 'po:1001',
    relNames: ['has_line_item', 'fulfilled_by', 'placed_with', 'covered_by'],
    maxDepth: 3,
    direction: 'outgoing',
  });
  console.log('--- 模板: 影响分析 (PO-1001) ---');
  console.log(impact.stats);
  console.log(impact.data.visual);

  // 模板: ownershipTree — 供应商 -> 仓库层级
  const ownership = await om.ownershipTree(db, {
    rootId: 's:acme',
    ownerRelNames: ['supplies', 'placed_with'],
    maxDepth: 3,
  });
  console.log('--- 模板: 所有权树 (先达公司) ---');
  console.log(ownership.stats);
  console.log(ownership.data.visual);

  // 模板: riskHotspot — 按 risk_score 排列合同
  const hotspots = await om.riskHotspot(db, {
    typeName: 'Contract',
    riskAttr: 'risk_score',
    topK: 5,
    minScore: 0,
    degreeWeight: 1,
  });
  console.log('--- 模板: 风险热点 (合同) ---');
  console.log(hotspots.data.hotspots);
  console.log(hotspots.data.visual);
}

async function showValidationDemos(db) {
  console.log('--- 约束校验演示 ---');

  // 1) 属性类型错误: rating 期望 Number，传入字符串
  try {
    await om.setProperty(db, 's:acme', 'rating', 'five-stars');
    console.log('[异常] 类型不匹配检查未能拒绝输入');
  } catch (error) {
    console.log(`[预期] 类型错误: ${error.message}`);
  }

  // 2) 未定义的属性
  try {
    await om.setProperty(db, 's:acme', 'founded_year', 1985);
    console.log('[异常] 未定义属性检查未能拒绝输入');
  } catch (error) {
    console.log(`[预期] 未定义属性: ${error.message}`);
  }

  // 3) 关系端点类型错误: placed_with 期望 PO->Supplier，尝试 Supplier->Warehouse
  try {
    await om.linkEntities(db, 's:acme', 'placed_with', 'wh:east');
    console.log('[异常] 关系类型检查未能拒绝输入');
  } catch (error) {
    console.log(`[预期] 端点错误: ${error.message}`);
  }

  // 4) 缺少必填属性 -> 修复 -> 最终确认成功
  await om.createEntity(db, 'po:draft', 'PurchaseOrder', '采购单-草稿');
  const draftValidation = await om.validateEntity(db, 'po:draft');
  console.log('--- 草稿采购单校验（修复前） ---');
  console.log(draftValidation);

  try {
    await om.finalizeEntity(db, 'po:draft');
    console.log('[异常] 必填属性检查未能拒绝输入');
  } catch (error) {
    console.log(`[预期] 缺少必填项: ${error.message}`);
  }

  // 修复: 填充必填字段
  await om.setProperty(db, 'po:draft', 'total_amount', 0);
  await om.setProperty(db, 'po:draft', 'status', 'draft');
  await om.finalizeEntity(db, 'po:draft');
  console.log('[成功] 采购单-草稿在填充必填属性后已最终确认');
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
