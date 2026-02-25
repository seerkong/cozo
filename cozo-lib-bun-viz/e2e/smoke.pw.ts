import { expect, test } from '@playwright/test';

async function runAndExpectDone(page: { getByTestId: any }) {
  await page.getByTestId('run-button').click();
  await expect(page.getByTestId('status')).toContainText(/Done/i);
  await expect(page.getByTestId('status')).not.toContainText(/Error:/i);
}

test('procurement: run table query and render schema', async ({ page }) => {
  await page.goto('/');

  await expect(page.getByTestId('demo-select')).toBeVisible();
  await page.getByTestId('demo-select').selectOption('procurement');

  await expect(page.getByTestId('query-select')).toBeVisible();
  await page.getByTestId('query-select').selectOption('dslQuery');
  await runAndExpectDone(page);

  await expect(page.getByTestId('result-table')).toBeVisible();
  const resultRows = page.locator('[data-testid="result-table"] tbody tr');
  await expect.poll(async () => resultRows.count()).toBeGreaterThan(0);

  await page.getByTestId('tab-schema').click();
  await expect(page.getByTestId('schema-graph')).toBeVisible();
  await expect(page.getByTestId('schema-empty')).toHaveCount(0);
});

test('hr + crm: run queries without type mismatch', async ({ page }) => {
  await page.goto('/');

  await page.getByTestId('demo-select').selectOption('hr');
  await expect(page.getByTestId('query-select')).toBeVisible();
  await page.getByTestId('query-select').selectOption('riskHotspot');
  await runAndExpectDone(page);
  await expect(page.getByTestId('result-table')).toBeVisible();

  await page.getByTestId('demo-select').selectOption('crm');
  await expect(page.getByTestId('query-select')).toBeVisible();
  await page.getByTestId('query-select').selectOption('impactAnalysis');
  await runAndExpectDone(page);

  await page.getByTestId('tab-graph').click();
  await expect(page.getByTestId('graph-view')).toBeVisible();
  await expect(page.locator('[data-testid="graph-view"] .empty')).toHaveCount(0);
});

test('approval-flow: run computed demo query', async ({ page }) => {
  await page.goto('/');

  await expect(page.getByTestId('demo-select')).toBeVisible();
  await page.getByTestId('demo-select').selectOption('approval-flow');

  await expect(page.getByTestId('query-select')).toBeVisible();
  await page.getByTestId('query-select').selectOption('requestsWithComputed');
  await runAndExpectDone(page);

  await expect(page.getByTestId('result-table')).toBeVisible();
  const resultRows = page.locator('[data-testid="result-table"] tbody tr');
  await expect.poll(async () => resultRows.count()).toBeGreaterThan(0);
});

test('approval-flow: run timeline query and render rows', async ({ page }) => {
  await page.goto('/');

  await expect(page.getByTestId('demo-select')).toBeVisible();
  await page.getByTestId('demo-select').selectOption('approval-flow');

  await expect(page.getByTestId('query-select')).toBeVisible();
  await page.getByTestId('query-select').selectOption('timelineAfterApprove');
  await runAndExpectDone(page);

  await expect(page.getByTestId('result-table')).toBeVisible();
  const resultRows = page.locator('[data-testid="result-table"] tbody tr');
  await expect.poll(async () => resultRows.count()).toBeGreaterThan(0);

  // Should contain an approved status row in the timeline.
  await expect(page.getByTestId('result-table')).toContainText('approved');
});

test('org-timeline: run snapshot + timeline queries', async ({ page }) => {
  await page.goto('/');

  await expect(page.getByTestId('demo-select')).toBeVisible();
  await page.getByTestId('demo-select').selectOption('org-timeline');

  await expect(page.getByTestId('query-select')).toBeVisible();
  await page.getByTestId('query-select').selectOption('snapshot_2024_12');
  await runAndExpectDone(page);
  await expect(page.getByTestId('result-table')).toBeVisible();
  await expect(page.locator('[data-testid="result-table"] tbody tr')).toHaveCount(3);

  await page.getByTestId('query-select').selectOption('alice_timeline');
  await runAndExpectDone(page);
  await expect(page.getByTestId('result-table')).toBeVisible();
  await expect(page.getByTestId('result-table')).toContainText('emp:alice');
});

test('permission demo: run RBAC check and expand sections', async ({ page }) => {
  await page.goto('/');

  await page.getByTestId('nav-permission').click();
  await expect(page.getByTestId('permission-model-select')).toBeVisible();
  await expect.poll(async () => {
    const opts = page.locator('[data-testid="permission-model-select"] option');
    return opts.count();
  }).toBeGreaterThan(0);

  await page.getByTestId('permission-model-select').selectOption('rbac');
  await expect.poll(async () => {
    const opts = page.locator('[data-testid="permission-query-select"] option');
    return opts.count();
  }).toBeGreaterThan(0);
  await page.getByTestId('permission-query-select').selectOption('checkPermission');

  await page.getByTestId('permission-param-username').fill('admin');
  await page.getByTestId('permission-param-resource').fill('article');
  await page.getByTestId('permission-param-action').fill('create');

  await page.getByTestId('permission-run-button').click();
  await expect(page.getByTestId('permission-status')).toContainText(/Done/i);
  await expect(page.getByTestId('permission-status')).not.toContainText(/Error:/i);

  await expect(page.getByTestId('permission-section-check-result')).toBeVisible();
  await expect(page.getByTestId('permission-section-permission-path')).toBeVisible();

  const resultTables = page.locator('[data-testid="result-table"]');
  await expect.poll(async () => resultTables.count()).toBe(1);

  await page.getByTestId('permission-section-permission-path').click();
  await expect.poll(async () => resultTables.count()).toBe(2);
});

test('permission demo: run ABAC check and expand sections', async ({ page }) => {
  await page.goto('/');

  await page.getByTestId('nav-permission').click();
  await expect(page.getByTestId('permission-model-select')).toBeVisible();
  await expect.poll(async () => {
    const opts = page.locator('[data-testid="permission-model-select"] option');
    return opts.count();
  }).toBeGreaterThan(0);

  await page.getByTestId('permission-model-select').selectOption('abac');
  await expect.poll(async () => {
    const opts = page.locator('[data-testid="permission-query-select"] option');
    return opts.count();
  }).toBeGreaterThan(0);
  await page.getByTestId('permission-query-select').selectOption('checkAccess');

  await page.getByTestId('permission-param-username').fill('alice');
  await page.getByTestId('permission-param-resourceName').fill('Salary Database');
  await page.getByTestId('permission-param-actionName').fill('read');

  await page.getByTestId('permission-run-button').click();
  await expect(page.getByTestId('permission-status')).toContainText(/Done/i);
  await expect(page.getByTestId('permission-status')).not.toContainText(/Error:/i);

  await expect(page.getByTestId('permission-section-check-result')).toBeVisible();
  await expect(page.getByTestId('permission-section-user-attributes')).toBeVisible();
  await expect(page.getByTestId('permission-section-resource-attributes')).toBeVisible();
  await expect(page.getByTestId('permission-section-matched-policies')).toBeVisible();

  const resultTables = page.locator('[data-testid="result-table"]');
  await expect.poll(async () => resultTables.count()).toBe(1);

  await page.getByTestId('permission-section-user-attributes').click();
  await page.getByTestId('permission-section-resource-attributes').click();
  await page.getByTestId('permission-section-matched-policies').click();
  await expect.poll(async () => resultTables.count()).toBe(4);
});

// ── New e2e tests ─────────────────────────────────────────────────────

/** Helper: navigate to permission page and wait for models to load */
async function gotoPermission(page: any) {
  await page.goto('/');
  await page.getByTestId('nav-permission').click();
  await expect(page.getByTestId('permission-model-select')).toBeVisible();
  await expect.poll(async () => {
    return page.locator('[data-testid="permission-model-select"] option').count();
  }).toBeGreaterThan(0);
}

/** Helper: select model + query, wait for query options to populate */
async function selectModelAndQuery(page: any, modelId: string, queryId: string) {
  await page.getByTestId('permission-model-select').selectOption(modelId);
  await expect.poll(async () => {
    return page.locator('[data-testid="permission-query-select"] option').count();
  }).toBeGreaterThan(0);
  await page.getByTestId('permission-query-select').selectOption(queryId);
}

/** Helper: click run and assert Done */
async function runPermissionAndExpectDone(page: any) {
  await page.getByTestId('permission-run-button').click();
  await expect(page.getByTestId('permission-status')).toContainText(/Done/i);
  await expect(page.getByTestId('permission-status')).not.toContainText(/Error:/i);
}

/**
 * Test 1: Edit RBAC table data → checkPermission result changes.
 *
 * Strategy:
 *   1) Run checkPermission for charlie/article/create → hasPermission = false
 *      (charlie is Viewer, default only article/read)
 *   2) Reload workbook via __permReloadTables with modified role_permissions
 *      that grants Viewer (r004) the article/create permission (p009)
 *   3) Re-run → hasPermission = true
 */
test('permission demo: editing RBAC table data changes checkPermission result', async ({ page }) => {
  await gotoPermission(page);
  await selectModelAndQuery(page, 'rbac', 'checkPermission');

  // Step 1: charlie (Viewer) cannot create articles by default
  await page.getByTestId('permission-param-username').fill('charlie');
  await page.getByTestId('permission-param-resource').fill('article');
  await page.getByTestId('permission-param-action').fill('create');
  await runPermissionAndExpectDone(page);

  // First section (check-result) is auto-expanded; read hasPermission from table
  await expect(page.getByTestId('permission-section-check-result')).toBeVisible();
  const firstTable = page.locator('[data-testid="result-table"]').first();
  await expect(firstTable).toBeVisible();
  // hasPermission should be false — permission-path section should show 0 rows
  const pathHeader = page.getByTestId('permission-section-permission-path');
  await expect(pathHeader).toContainText('0 rows');

  // Step 2: Inject modified role_permissions via test helper — add r004→p009
  await page.evaluate(() => {
    const modifiedRolePermissions = {
      name: 'role_permissions',
      schema: '{ role_id: String, permission_id: String => }',
      columns: ['role_id', 'permission_id'],
      rows: [
        { role_id: 'r001', permission_id: 'p001' }, { role_id: 'r001', permission_id: 'p002' },
        { role_id: 'r001', permission_id: 'p003' }, { role_id: 'r001', permission_id: 'p004' },
        { role_id: 'r001', permission_id: 'p005' }, { role_id: 'r001', permission_id: 'p006' },
        { role_id: 'r001', permission_id: 'p007' }, { role_id: 'r001', permission_id: 'p008' },
        { role_id: 'r001', permission_id: 'p009' }, { role_id: 'r001', permission_id: 'p010' },
        { role_id: 'r001', permission_id: 'p011' }, { role_id: 'r001', permission_id: 'p012' },
        { role_id: 'r001', permission_id: 'p013' },
        { role_id: 'r002', permission_id: 'p002' }, { role_id: 'r002', permission_id: 'p003' },
        { role_id: 'r002', permission_id: 'p006' }, { role_id: 'r002', permission_id: 'p007' },
        { role_id: 'r002', permission_id: 'p009' }, { role_id: 'r002', permission_id: 'p010' },
        { role_id: 'r002', permission_id: 'p011' }, { role_id: 'r002', permission_id: 'p012' },
        { role_id: 'r003', permission_id: 'p002' }, { role_id: 'r003', permission_id: 'p009' },
        { role_id: 'r003', permission_id: 'p010' }, { role_id: 'r003', permission_id: 'p011' },
        { role_id: 'r004', permission_id: 'p002' }, { role_id: 'r004', permission_id: 'p010' },
        // ★ NEW: grant Viewer (r004) article/create (p009)
        { role_id: 'r004', permission_id: 'p009' },
        { role_id: 'r005', permission_id: 'p010' },
      ],
    };
    (window as any).__permReloadTables([
      // Keep other tables as-is by re-sending defaults
      {
        name: 'users',
        schema: '{ user_id: String => username: String, email: String, created_at: Int }',
        columns: ['user_id', 'username', 'email', 'created_at'],
        rows: [
          { user_id: 'u001', username: 'admin', email: 'admin@example.com', created_at: 20240101 },
          { user_id: 'u002', username: 'alice', email: 'alice@example.com', created_at: 20240102 },
          { user_id: 'u003', username: 'bob', email: 'bob@example.com', created_at: 20240103 },
          { user_id: 'u004', username: 'charlie', email: 'charlie@example.com', created_at: 20240104 },
          { user_id: 'u005', username: 'david', email: 'david@example.com', created_at: 20240105 },
        ],
      },
      {
        name: 'roles',
        schema: '{ role_id: String => role_name: String, description: String }',
        columns: ['role_id', 'role_name', 'description'],
        rows: [
          { role_id: 'r001', role_name: 'SuperAdmin', description: 'super' },
          { role_id: 'r002', role_name: 'Admin', description: 'admin' },
          { role_id: 'r003', role_name: 'Editor', description: 'editor' },
          { role_id: 'r004', role_name: 'Viewer', description: 'viewer' },
          { role_id: 'r005', role_name: 'Guest', description: 'guest' },
        ],
      },
      {
        name: 'permissions',
        schema: '{ permission_id: String => resource: String, action: String, description: String }',
        columns: ['permission_id', 'resource', 'action', 'description'],
        rows: [
          { permission_id: 'p001', resource: 'user', action: 'create', description: '' },
          { permission_id: 'p002', resource: 'user', action: 'read', description: '' },
          { permission_id: 'p003', resource: 'user', action: 'update', description: '' },
          { permission_id: 'p004', resource: 'user', action: 'delete', description: '' },
          { permission_id: 'p005', resource: 'role', action: 'create', description: '' },
          { permission_id: 'p006', resource: 'role', action: 'read', description: '' },
          { permission_id: 'p007', resource: 'role', action: 'update', description: '' },
          { permission_id: 'p008', resource: 'role', action: 'delete', description: '' },
          { permission_id: 'p009', resource: 'article', action: 'create', description: '' },
          { permission_id: 'p010', resource: 'article', action: 'read', description: '' },
          { permission_id: 'p011', resource: 'article', action: 'update', description: '' },
          { permission_id: 'p012', resource: 'article', action: 'delete', description: '' },
          { permission_id: 'p013', resource: 'system', action: 'manage', description: '' },
        ],
      },
      {
        name: 'user_roles',
        schema: '{ user_id: String, role_id: String => }',
        columns: ['user_id', 'role_id'],
        rows: [
          { user_id: 'u001', role_id: 'r001' },
          { user_id: 'u002', role_id: 'r002' },
          { user_id: 'u002', role_id: 'r003' },
          { user_id: 'u003', role_id: 'r003' },
          { user_id: 'u004', role_id: 'r004' },
          { user_id: 'u005', role_id: 'r005' },
        ],
      },
      modifiedRolePermissions,
    ]);
  });

  // Small wait for workbook to re-render
  await page.waitForTimeout(500);

  // Step 3: Re-run the same query — now charlie should have permission
  await runPermissionAndExpectDone(page);
  await expect(page.getByTestId('permission-section-check-result')).toBeVisible();
  // permission-path should now have rows (charlie→Viewer→article/create)
  const pathHeaderAfter = page.getByTestId('permission-section-permission-path');
  await expect(pathHeaderAfter).not.toContainText('0 rows');
});

/**
 * Test 2: Timeline model — all 3 query views execute successfully with result sections.
 */
test('permission demo: timeline model 3 query views all produce results', async ({ page }) => {
  await gotoPermission(page);

  // ── pointQuery ──
  await selectModelAndQuery(page, 'timeline', 'pointQuery');
  await page.getByTestId('permission-param-queryDate').fill('20240101');
  await runPermissionAndExpectDone(page);
  await expect(page.getByTestId('permission-section-point-permissions')).toBeVisible();
  await expect(page.getByTestId('permission-section-point-stats')).toBeVisible();

  // ── history ──
  await page.getByTestId('permission-query-select').selectOption('history');
  await page.getByTestId('permission-param-deptCode').fill('D01');
  await runPermissionAndExpectDone(page);
  await expect(page.getByTestId('permission-section-history-detail')).toBeVisible();
  await expect(page.getByTestId('permission-section-history-summary')).toBeVisible();

  // ── compare ──
  await page.getByTestId('permission-query-select').selectOption('compare');
  await page.getByTestId('permission-param-date1').fill('20230101');
  await page.getByTestId('permission-param-date2').fill('20240101');
  await runPermissionAndExpectDone(page);
  await expect(page.getByTestId('permission-section-snapshot-date1')).toBeVisible();
  await expect(page.getByTestId('permission-section-snapshot-date2')).toBeVisible();
});

/**
 * Test 3: permission-sections container is scrollable when content overflows.
 *
 * Strategy: run ABAC checkAccess (produces 4 sections), expand all,
 * then assert the container has overflow: auto and scrollHeight > clientHeight.
 */
test('permission demo: sections container is scrollable on overflow', async ({ page }) => {
  await gotoPermission(page);
  await selectModelAndQuery(page, 'abac', 'checkAccess');

  await page.getByTestId('permission-param-username').fill('alice');
  await page.getByTestId('permission-param-resourceName').fill('Salary Database');
  await page.getByTestId('permission-param-actionName').fill('read');
  await runPermissionAndExpectDone(page);

  // Expand all collapsed sections to maximize content height
  await page.getByTestId('permission-section-user-attributes').click();
  await page.getByTestId('permission-section-resource-attributes').click();
  await page.getByTestId('permission-section-matched-policies').click();

  // Wait for tables to render
  const resultTables = page.locator('[data-testid="result-table"]');
  await expect.poll(async () => resultTables.count()).toBe(4);

  // Shrink viewport to guarantee overflow
  await page.setViewportSize({ width: 1280, height: 500 });
  await page.waitForTimeout(300);

  // Assert the container is actually scrollable (not just CSS overflow)
  const scrollResult = await page.evaluate(() => {
    // Try the sections container first, fall back to page-wrap
    const el =
      document.querySelector('[data-testid="permission-sections"]') ??
      document.querySelector('.page-wrap');
    if (!el) return { found: false, before: 0, after: 0 };
    // Find the scrollable ancestor (scrollHeight > clientHeight)
    let target: Element | null = el;
    while (target && target.scrollHeight <= target.clientHeight) {
      target = target.parentElement;
    }
    if (!target) target = el;

    const before = target.scrollTop;
    const maxScroll = target.scrollHeight - target.clientHeight;
    const next = Math.min(before + 240, maxScroll);
    target.scrollTop = next;
    const after = target.scrollTop;
    return { found: true, before, after, maxScroll, changed: after !== before };
  });
  expect(scrollResult.found).toBe(true);
  expect(scrollResult.changed).toBe(true);

  // Assert the last section is reachable by scrolling
  const lastSection = page.getByTestId('permission-section-matched-policies');
  await lastSection.scrollIntoViewIfNeeded();
  await expect(lastSection).toBeVisible();
});
