import { test, expect } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { stubApi } from './fixtures/api';

test.describe('UI pilot a11y', () => {
  test.beforeEach(async ({ page }) => { await stubApi(page); });

  test('main shell header has no axe violations', async ({ page }) => {
    await page.goto('/');
    const results = await new AxeBuilder({ page })
      .include('header')
      .analyze();
    expect(results.violations.filter((v) => v.impact === 'critical' || v.impact === 'serious')).toEqual([]);
  });

  test('chess lab view loads', async ({ page }) => {
    await page.goto('/chess');
    await page.getByRole('link', { name: 'Lab', exact: true }).click();
    await expect(page).toHaveURL(/\/lab$/);
    await expect(page.getByRole('heading', { name: 'Chess Lab' })).toBeVisible({ timeout: 15_000 });
  });
});
