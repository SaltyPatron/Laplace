import { test, expect } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import nativeClaims from './fixtures/native-claim-leaders.json' with { type: 'json' };
import { stubApi } from './fixtures/api';

test.beforeEach(async ({ page }) => { await stubApi(page); });

test('App header has no critical a11y violations', async ({ page }) => {
  await page.goto('/');
  const results = await new AxeBuilder({ page })
    .include('header')
    .analyze();
  expect(results.violations.filter((v) => v.impact === 'critical' || v.impact === 'serious')).toEqual([]);
});

// The landing is Look up: the knowledge search with recorded assertions sampled from consensus.
test('Look up landing has no critical a11y violations', async ({ page }) => {
  await page.route('**/v1/query/leaders/home', (route) => route.fulfill({ json: nativeClaims }));
  await page.goto('/');
  await expect(page).toHaveURL(/\/explore$/);
  await expect(page.getByRole('textbox', { name: 'Search knowledge' })).toBeVisible({ timeout: 15_000 });
  await expect(page.locator('[data-claim-id]').first()).toBeVisible();
  const results = await new AxeBuilder({ page })
    .include('main')
    .analyze();
  expect(results.violations.filter((v) => v.impact === 'critical' || v.impact === 'serious')).toEqual([]);
});

test('Primary navigation rail has no critical a11y violations', async ({ page }) => {
  await page.goto('/chess');
  await expect(page.getByRole('link', { name: 'Lab', exact: true })).toBeVisible({ timeout: 15_000 });
  const results = await new AxeBuilder({ page })
    .include('nav[aria-label="Primary navigation"]')
    .analyze();
  expect(results.violations.filter((v) => v.impact === 'critical' || v.impact === 'serious')).toEqual([]);
});

test('Chess Lab tab loads with accessible form controls', async ({ page }) => {
  await page.goto('/chess');
  await page.getByRole('link', { name: 'Lab', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Chess Lab' })).toBeVisible({ timeout: 15_000 });
  const results = await new AxeBuilder({ page })
    .include('main')
    .analyze();
  expect(results.violations.filter((v) => v.impact === 'critical' || v.impact === 'serious')).toEqual([]);
});

test('Engine Gauntlet surface has no critical a11y violations', async ({ page }) => {
  await page.goto('/lab/gauntlet');
  await expect(page.getByRole('heading', { name: 'Engine Gauntlet' })).toBeVisible({ timeout: 15_000 });
  const results = await new AxeBuilder({ page })
    .include('main')
    .analyze();
  expect(results.violations.filter((v) => v.impact === 'critical' || v.impact === 'serious')).toEqual([]);
});
