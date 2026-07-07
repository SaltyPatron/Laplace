import { test, expect } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';

test.describe('UI pilot a11y', () => {
  test('main shell header has no axe violations', async ({ page }) => {
    await page.goto('/');
    const results = await new AxeBuilder({ page })
      .include('header')
      .analyze();
    expect(results.violations).toEqual([]);
  });

  test('chess lab view loads', async ({ page }) => {
    await page.goto('/');
    await page.getByRole('button', { name: 'Lab' }).click();
    await expect(page.getByRole('heading', { name: 'Chess Lab' })).toBeVisible();
  });
});
