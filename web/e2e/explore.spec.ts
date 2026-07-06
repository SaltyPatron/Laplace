import { test, expect } from '@playwright/test';

test('Explore tab loads warehouse', async ({ page }) => {
  await page.goto('/explore');
  await expect(page.locator('h2')).toContainText('Substrate warehouse');
  await expect(page.locator('.explore-search input')).toBeVisible();
});

test('Explore resolve navigates to entity preview', async ({ page }) => {
  await page.goto('/explore');
  await page.locator('.explore-search input').fill('whale');
  await page.locator('.explore-search button').click();
  await expect(page).toHaveURL(/\/explore\/entity\/[0-9a-f]{32}/i, { timeout: 15_000 });
  await expect(page.locator('.entity-header h2')).toBeVisible({ timeout: 15_000 });
});

test('Glome canvas mounts after unlock', async ({ page }) => {
  await page.goto('/explore/resolve/whale');
  await expect(page.locator('.entity-header h2')).toBeVisible({ timeout: 15_000 });
  await page.locator('.gate-prompt button').first().click();
  await page.locator('.detail-tabs button', { hasText: 'glome' }).click();
  await page.locator('.gate-prompt button').first().click();
  await expect(page.locator('.glome-canvas canvas')).toBeVisible({ timeout: 15_000 });
});

test('Gated expand shows GatePrompt', async ({ page }) => {
  await page.goto('/explore/resolve/whale');
  await expect(page.locator('.gate-prompt')).toBeVisible({ timeout: 15_000 });
  await expect(page.locator('.gate-prompt')).toContainText('inspect');
});
