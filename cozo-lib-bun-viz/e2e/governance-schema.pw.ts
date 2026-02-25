import { expect, test } from '@playwright/test';

async function setNumberOrSelect(locator: any, value: number) {
  const tag = await locator.evaluate((el: Element) => el.tagName.toLowerCase());
  if (tag === 'select') {
    await locator.selectOption({ value: String(value) });
    return;
  }
  await locator.fill(String(value));
}

async function waitForNoErrorStatus(page: any, testId: string, timeout = 60_000) {
  await expect
    .poll(
      async () => {
        const t = await page.getByTestId(testId).innerText();
        return String(t || '').trim();
      },
      { timeout }
    )
    .not.toMatch(/\bError\b|failed|exception|traceback/i);
}

async function waitForStatusContains(page: any, testId: string, re: RegExp, timeout = 60_000) {
  await expect.poll(async () => {
    const t = await page.getByTestId(testId).innerText();
    return String(t || '').trim();
  }, { timeout }).toMatch(re);
}

test.describe.serial('governance + schema pages', () => {
  test('governance: seed + run produces Done and allow=true', async ({ page }) => {
    test.setTimeout(120_000);

    await page.goto('/');
    await expect(page.getByTestId('nav-governance')).toBeVisible({ timeout: 60_000 });
    await page.getByTestId('nav-governance').click();

    // Governance sub-tabs should exist and be switchable.
    await expect(page.getByTestId('governance-tab-prep')).toBeVisible();
    await expect(page.getByTestId('governance-tab-query')).toBeVisible();
    await page.getByTestId('governance-tab-prep').click();
    await expect(page.getByTestId('governance-seed-workbook')).toBeVisible();

    // Seed is done via the data-prep tab (query tab has no Seed button).
    await expect(page.getByTestId('governance-seed-load-template')).toBeVisible();
    await page.getByTestId('governance-seed-load-template').click();
    await expect(page.getByTestId('governance-seed-apply')).toBeEnabled({ timeout: 60_000 });
    await page.getByTestId('governance-seed-apply').click();

    // Wait for seeding to complete (tolerate warm-up).
    await expect(page.getByTestId('governance-status')).toBeVisible({ timeout: 60_000 });
    await waitForNoErrorStatus(page, 'governance-status', 60_000);
    await waitForStatusContains(page, 'governance-status', /Seeded/i, 60_000);

    await expect(page.getByTestId('governance-run')).toBeVisible();
    await page.getByTestId('governance-run').click();

    await waitForStatusContains(page, 'governance-status', /Done/i, 60_000);
    await expect(page.getByTestId('governance-status')).not.toContainText(/\bError\b|failed/i);

    // Either decision pill shows ALLOW, or raw JSON contains allow:true.
    await expect.poll(async () => {
      const decision = await page.getByTestId('governance-decision').innerText().catch(() => '');
      const raw = await page.getByTestId('governance-raw').innerText().catch(() => '');
      const hasAllowPill = /\bALLOW\b/i.test(String(decision || ''));
      const hasAllowInJson = /"allow"\s*:\s*true|\ballow\s*:\s*true/i.test(String(raw || ''));
      return hasAllowPill || hasAllowInJson;
    }, { timeout: 60_000 }).toBe(true);
  });

  test('schema: refresh, apply default migration, diff non-empty, rollback to v1 strict', async ({ page }) => {
    test.setTimeout(180_000);

    await page.goto('/');
    await expect(page.getByTestId('nav-schema')).toBeVisible({ timeout: 60_000 });
    await page.getByTestId('nav-schema').click();

    await expect(page.getByTestId('schema-state-refresh')).toBeVisible();
    await expect(page.getByTestId('schema-versions-refresh')).toBeVisible();

    // Refresh state + versions explicitly for test determinism.
    await page.getByTestId('schema-state-refresh').click();
    await page.getByTestId('schema-versions-refresh').click();

    await waitForStatusContains(page, 'schema-status-state', /^OK/i, 60_000);
    await waitForStatusContains(page, 'schema-status-versions', /^OK/i, 60_000);

    // Ensure we start from v1; if not, rollback first (helps when rerunning locally).
    await expect.poll(async () => {
      const txt = await page.getByTestId('schema-current-version').innerText();
      const n = Number(String(txt || '').trim());
      return Number.isFinite(n) ? n : -1;
    }, { timeout: 60_000 }).toBeGreaterThan(0);

    const cv0 = Number((await page.getByTestId('schema-current-version').innerText()).trim());
    if (cv0 !== 1) {
      await setNumberOrSelect(page.getByTestId('schema-rollback-target'), 1);
      await page.getByTestId('schema-rollback-strict').setChecked(true);
      await page.getByTestId('schema-rollback-run').click();
      await waitForStatusContains(page, 'schema-status-rollback', /^OK/i, 90_000);
      await expect.poll(async () => {
        const txt = await page.getByTestId('schema-current-version').innerText();
        return Number(String(txt || '').trim());
      }, { timeout: 90_000 }).toBe(1);
    }

    // Apply migration (default spec already filled).
    await expect.poll(async () => {
      const spec = await page.getByTestId('schema-migration-spec').inputValue();
      return String(spec || '').trim().length;
    }, { timeout: 30_000 }).toBeGreaterThan(0);

    await page.getByTestId('schema-apply-run').click();
    await waitForStatusContains(page, 'schema-status-apply', /^OK/i, 120_000);

    // Diff from v1 to v2; ensure JSON response is non-empty.
    await setNumberOrSelect(page.getByTestId('schema-diff-from'), 1);
    await setNumberOrSelect(page.getByTestId('schema-diff-to'), 2);
    await page.getByTestId('schema-diff-run').click();
    await waitForStatusContains(page, 'schema-status-diff', /^OK/i, 90_000);

    const diffText = await page.getByTestId('schema-diff-json').innerText();
    const diffObj = JSON.parse(diffText);
    expect(diffObj && typeof diffObj === 'object').toBe(true);
    expect(diffObj.diff && typeof diffObj.diff === 'object').toBe(true);
    expect(JSON.stringify(diffObj.diff)).not.toBe('{}');

    // Roll back to v1 (strict=true) and assert currentVersion becomes 1.
    await setNumberOrSelect(page.getByTestId('schema-rollback-target'), 1);
    await page.getByTestId('schema-rollback-strict').setChecked(true);
    await page.getByTestId('schema-rollback-run').click();
    await waitForStatusContains(page, 'schema-status-rollback', /^OK/i, 120_000);

    await expect.poll(async () => {
      const txt = await page.getByTestId('schema-current-version').innerText();
      return Number(String(txt || '').trim());
    }, { timeout: 120_000 }).toBe(1);
  });
});
