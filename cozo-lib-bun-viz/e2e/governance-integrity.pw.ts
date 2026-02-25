import { expect, test } from '@playwright/test';

async function waitForStatusContains(page: any, testId: string, re: RegExp, timeout = 60_000) {
  await expect.poll(async () => {
    const t = await page.getByTestId(testId).innerText();
    return String(t || '').trim();
  }, { timeout }).toMatch(re);
}

test.describe.serial('governance integrity tab (existential rules)', () => {
  test('integrity: seed-demo -> check shows violations -> apply clears them', async ({ page }) => {
    test.setTimeout(120_000);

    await page.goto('/');
    await expect(page.getByTestId('nav-governance')).toBeVisible({ timeout: 60_000 });
    await page.getByTestId('nav-governance').click();

    // The new tab coexists with the existing two tabs.
    await expect(page.getByTestId('governance-tab-prep')).toBeVisible();
    await expect(page.getByTestId('governance-tab-query')).toBeVisible();
    await expect(page.getByTestId('governance-tab-integrity')).toBeVisible();
    await page.getByTestId('governance-tab-integrity').click();

    // Seed the demo: rule + orphan assets.
    await expect(page.getByTestId('integrity-seed')).toBeVisible();
    await page.getByTestId('integrity-seed').click();
    await waitForStatusContains(page, 'integrity-status', /已初始化/, 60_000);
    await expect(page.getByTestId('integrity-rules')).toContainText('asset_must_have_owner');

    // Detect violations: the two orphan assets are listed.
    await page.getByTestId('integrity-check').click();
    await waitForStatusContains(page, 'integrity-status', /2 条违例/, 60_000);
    await expect(page.getByTestId('integrity-violations')).toContainText('a:orphan-1');
    await expect(page.getByTestId('integrity-violations')).toContainText('a:orphan-2');

    // One-click chase: Skolem entities created, violations cleared.
    await page.getByTestId('integrity-apply').click();
    await waitForStatusContains(page, 'integrity-status', /物化完成/, 60_000);
    await expect(page.getByTestId('integrity-created')).toContainText('skolem:');
    await expect(page.getByTestId('integrity-no-violations')).toBeVisible();
  });

  test('integrity tab does not affect the existing governance query flow', async ({ page }) => {
    test.setTimeout(120_000);

    await page.goto('/');
    await page.getByTestId('nav-governance').click();

    // Touch the integrity tab first.
    await page.getByTestId('governance-tab-integrity').click();
    await page.getByTestId('integrity-seed').click();
    await waitForStatusContains(page, 'integrity-status', /已初始化/, 60_000);

    // The pre-existing prep + query flow still works end to end.
    await page.getByTestId('governance-tab-prep').click();
    await expect(page.getByTestId('governance-seed-load-template')).toBeVisible();
    await page.getByTestId('governance-seed-load-template').click();
    await expect(page.getByTestId('governance-seed-apply')).toBeEnabled({ timeout: 60_000 });
    await page.getByTestId('governance-seed-apply').click();
    await waitForStatusContains(page, 'governance-status', /Seeded/i, 60_000);

    await expect(page.getByTestId('governance-run')).toBeVisible();
    await page.getByTestId('governance-run').click();
    await waitForStatusContains(page, 'governance-status', /Done/i, 60_000);

    // Same tolerant assertion as governance-schema.pw.ts: either the decision
    // pill shows ALLOW, or the raw JSON carries allow:true.
    await expect.poll(async () => {
      const decision = await page.getByTestId('governance-decision').innerText().catch(() => '');
      const raw = await page.getByTestId('governance-raw').innerText().catch(() => '');
      const hasAllowPill = /\bALLOW\b/i.test(String(decision || ''));
      const hasAllowInJson = /"allow"\s*:\s*true|\ballow\s*:\s*true/i.test(String(raw || ''));
      return hasAllowPill || hasAllowInJson;
    }, { timeout: 60_000 }).toBe(true);
  });
});
