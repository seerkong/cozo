const { CozoDb, om } = require('.');

async function main() {
  const db = new CozoDb();
  try {
    await om.initSchema(db);

    // ─── Type hierarchy ──────────────────────────────────────────────
    await om.defineType(db, 'Asset', '\u57fa\u7840\u8d44\u4ea7\u7c7b\u578b');
    await om.defineType(db, 'ITAsset', 'IT\u57fa\u7840\u8bbe\u65bd\u8d44\u4ea7', { parentType: 'Asset' });
    await om.defineType(db, 'Server', '\u7269\u7406\u6216\u865a\u62df\u670d\u52a1\u5668', { parentType: 'ITAsset' });

    // ─── Mixin ───────────────────────────────────────────────────────
    await om.defineMixin(db, 'Auditable', '\u8ddf\u8e2a\u5ba1\u8ba1\u5143\u6570\u636e');
    await om.defineAttribute(db, 'Auditable', 'last_audited', 'String', false, '\u6700\u540e\u5ba1\u8ba1\u65f6\u95f4');
    await om.defineAttribute(db, 'Auditable', 'audit_status', 'String', false, '\u5ba1\u8ba1\u72b6\u6001');

    // ─── Attributes on Asset (inherited by subtypes) ─────────────────
    await om.defineAttribute(db, 'Asset', 'value', 'Number', false, '\u8d44\u4ea7\u4ef7\u503c');
    await om.defineAttribute(db, 'Asset', 'location', 'String', false, '\u8d44\u4ea7\u4f4d\u7f6e');

    // ─── ITAsset-specific attribute ──────────────────────────────────
    await om.defineAttribute(db, 'ITAsset', 'ip_address', 'String', false, 'IP\u5730\u5740');

    // ─── Server-specific attribute ───────────────────────────────────
    await om.defineAttribute(db, 'Server', 'cpu_cores', 'Number', false, 'CPU\u6838\u5fc3\u6570');

    // ─── Relations ───────────────────────────────────────────────────
    await om.defineRelation(db, 'depends_on', 'Asset', 'Asset', true, '\u4f9d\u8d56\u5173\u7cfb');

    // ─── Entities ────────────────────────────────────────────────────
    // 2 Asset
    await om.createEntity(db, 'asset:desk', 'Asset', 'Standing Desk');
    await om.setProperty(db, 'asset:desk', 'value', 800);
    await om.setProperty(db, 'asset:desk', 'location', 'Floor 3');

    await om.createEntity(db, 'asset:chair', 'Asset', 'Ergonomic Chair');
    await om.setProperty(db, 'asset:chair', 'value', 600);
    await om.setProperty(db, 'asset:chair', 'location', 'Floor 3');

    // 3 ITAsset
    await om.createEntity(db, 'it:switch01', 'ITAsset', 'Core Switch 01');
    await om.setProperty(db, 'it:switch01', 'value', 12000);
    await om.setProperty(db, 'it:switch01', 'ip_address', '10.0.0.1');

    await om.createEntity(db, 'it:switch02', 'ITAsset', 'Core Switch 02');
    await om.setProperty(db, 'it:switch02', 'value', 12000);
    await om.setProperty(db, 'it:switch02', 'ip_address', '10.0.0.2');

    await om.createEntity(db, 'it:firewall', 'ITAsset', 'Edge Firewall');
    await om.setProperty(db, 'it:firewall', 'value', 25000);
    await om.setProperty(db, 'it:firewall', 'ip_address', '10.0.0.254');

    // 1 Server
    await om.createEntity(db, 'srv:prod01', 'Server', 'Production Server 01');
    await om.setProperty(db, 'srv:prod01', 'value', 45000);
    await om.setProperty(db, 'srv:prod01', 'ip_address', '10.0.1.10');
    await om.setProperty(db, 'srv:prod01', 'cpu_cores', 64);

    // ─── Edges ───────────────────────────────────────────────────────
    await om.linkEntities(db, 'srv:prod01', 'depends_on', 'it:switch01');
    await om.linkEntities(db, 'srv:prod01', 'depends_on', 'it:firewall');
    await om.linkEntities(db, 'it:switch01', 'depends_on', 'it:firewall');
    await om.linkEntities(db, 'it:switch02', 'depends_on', 'it:firewall');

    console.log('=== Data seeded ===\n');

    // ─── Type hierarchy queries ──────────────────────────────────────
    const ancestors = await om.getAncestors(db, 'Server');
    console.log('--- getAncestors("Server") ---');
    console.log(ancestors);

    const descendants = await om.getDescendants(db, 'Asset');
    console.log('\n--- getDescendants("Asset") ---');
    console.log(descendants);

    const isSub = await om.isSubtypeOf(db, 'Server', 'Asset');
    console.log('\n--- isSubtypeOf("Server", "Asset") ---');
    console.log(isSub);

    const notSub = await om.isSubtypeOf(db, 'Asset', 'Server');
    console.log('\n--- isSubtypeOf("Asset", "Server") ---');
    console.log(notSub);

    // ─── Polymorphic findByType ──────────────────────────────────────
    const allAssets = await om.findByType(db, 'Asset');
    console.log('\n--- findByType("Asset") [polymorphic] ---');
    console.log(`Count: ${allAssets.length}`);
    for (const e of allAssets) {
      console.log(`  ${e.id}  ${e.label}  value=${e.properties.value}`);
    }

    const exactAssets = await om.findByType(db, 'Asset', {}, { exact: true });
    console.log('\n--- findByType("Asset", {}, { exact: true }) ---');
    console.log(`Count: ${exactAssets.length}`);
    for (const e of exactAssets) {
      console.log(`  ${e.id}  ${e.label}  value=${e.properties.value}`);
    }

    // ─── Aggregate ───────────────────────────────────────────────────
    const totalValue = await om.aggregateByType(db, 'Asset', 'value', 'sum');
    const avgValue = await om.aggregateByType(db, 'Asset', 'value', 'avg');
    console.log('\n--- aggregateByType("Asset", "value") ---');
    console.log(`  sum = ${totalValue}`);
    console.log(`  avg = ${avgValue}`);

    // ─── impactAnalysis ──────────────────────────────────────────────
    const impact = await om.impactAnalysis(db, {
      rootId: 'it:firewall',
      relNames: ['depends_on'],
      maxDepth: 3,
      direction: 'incoming',
    });
    console.log('\n--- impactAnalysis("it:firewall", depends_on, incoming) ---');
    console.log(impact.stats);
    console.log(JSON.stringify(impact.data.visual.graph.adjacency, null, 2));

    // ─── ownershipTree ───────────────────────────────────────────────
    const tree = await om.ownershipTree(db, {
      rootId: 'srv:prod01',
      ownerRelNames: ['depends_on'],
      maxDepth: 3,
    });
    console.log('\n--- ownershipTree("srv:prod01", depends_on) ---');
    console.log(tree.stats);
    console.log(JSON.stringify(tree.data.visual.tree, null, 2));

    console.log('\n=== Demo complete ===');
  } finally {
    db.close();
  }
}

main().catch((e) => {
  console.error(e.display || e.message || e);
  process.exitCode = 1;
});
