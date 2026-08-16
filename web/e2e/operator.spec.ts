import { expect, test } from '@playwright/test';

/**
 * The operator console, rendered.
 *
 * Every other check on this surface passes without a browser: the components
 * typecheck, the bundle builds, the endpoints answer curl. None of that proves a
 * section renders — a throw inside one component blanks the tab while the build
 * stays green, which is the failure this file exists to catch.
 *
 * These run against a live substrate: Activity reads ops.activity, Repair reads
 * ops.index_health, Agents reads the routing catalog. Row COUNTS are deliberately
 * not asserted — a healthy substrate has zero invalid indexes and may have zero
 * long-running backends, so asserting rows would fail on a working system. What is
 * asserted is that each section reaches its surface and renders an answer rather
 * than an error.
 */
test.describe('operator console', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto('/operator');
    await expect(page.getByRole('heading', { name: 'Operator' })).toBeVisible();
  });

  test('warns that it is not a privilege boundary', async ({ page }) => {
    // The banner is load-bearing, not decoration: with auth stubbed every
    // operation here is reachable over plain HTTP without the console.
    await expect(page.getByText(/Privileges are not enforced/i)).toBeVisible();
  });

  test('exposes all five sections', async ({ page }) => {
    for (const section of ['ingest', 'activity', 'ops', 'repair', 'agents']) {
      await expect(page.getByRole('radio', { name: section, exact: true })).toBeVisible();
    }
  });

  test('activity renders live backends from ops.activity', async ({ page }) => {
    await page.getByRole('radio', { name: 'activity', exact: true }).click();
    await expect(page.getByRole('heading', { name: /^Activity/ })).toBeVisible();
    // Either a table of backends or the explicit empty statement — never the
    // loading text left stranded, which is what a failed op call looks like.
    await expect(
      page.getByRole('columnheader', { name: 'pid' })
        .or(page.getByText(/No backend matches this filter/i)),
    ).toBeVisible({ timeout: 15_000 });
    await expect(page.getByText(/Reading ops\.activity/)).toHaveCount(0);
  });

  test('repair renders index health and its repair actions', async ({ page }) => {
    await page.getByRole('radio', { name: 'repair', exact: true }).click();
    await expect(page.getByRole('heading', { name: /^Index health/ })).toBeVisible();
    await expect(
      page.getByText(/No invalid index/i).or(page.getByRole('columnheader', { name: 'index' })),
    ).toBeVisible({ timeout: 15_000 });
    await expect(page.getByRole('button', { name: 'Rebuild invalid indexes' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'ANALYZE substrate' })).toBeVisible();
  });

  test('agents renders the routing table without leaking a credential', async ({ page }) => {
    await page.getByRole('radio', { name: 'agents', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'Routes' })).toBeVisible();
    await expect(page.getByRole('columnheader', { name: 'credential' })).toBeVisible({
      timeout: 15_000,
    });

    // Every installed provider is a row, credentialed or not.
    await expect(page.getByRole('cell', { name: 'anthropic', exact: true }).first()).toBeVisible();

    // The table reports the VARIABLE NAME and a verdict. A key value on this page
    // would be a real leak: the console is unauthenticated.
    const body = (await page.locator('body').innerText()).toLowerCase();
    expect(body).toContain('anthropic_api_key');
    expect(body).not.toMatch(/sk-[a-z0-9]{16,}/);
  });

  test('ops console badges writes from the server policy, not a name regex', async ({ page }) => {
    await page.getByRole('radio', { name: 'ops', exact: true }).click();
    await expect(page.getByRole('heading', { name: /^Catalog/ })).toBeVisible({ timeout: 15_000 });
    await page.getByPlaceholder('filter by name…').fill('evict_source');
    // ops.evict_source matches no write-ish substring, so a regex would badge it
    // as harmless. It is the one operation on the surface that destroys testimony.
    await expect(page.getByText('destroys data')).toBeVisible({ timeout: 15_000 });
  });
});
