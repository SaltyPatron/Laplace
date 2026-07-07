import { test, expect } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';

test('App header has no critical a11y violations', async ({ page }) => {
  await page.goto('/');
  const results = await new AxeBuilder({ page })
    .include('header')
    .analyze();
  expect(results.violations.filter((v) => v.impact === 'critical' || v.impact === 'serious')).toEqual([]);
});

test('Chess Lab tab loads with accessible form controls', async ({ page }) => {
  await page.goto('/');
  await page.getByRole('button', { name: 'Lab' }).click();
  await expect(page.getByRole('heading', { name: 'Chess Lab' })).toBeVisible();
  const results = await new AxeBuilder({ page })
    .include('.chess-lab')
    .analyze();
  expect(results.violations.filter((v) => v.impact === 'critical' || v.impact === 'serious')).toEqual([]);
});
