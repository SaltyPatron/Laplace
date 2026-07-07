import { test, expect } from '@playwright/test';

test('Explore tab loads warehouse', async ({ page }) => {
  await page.goto('/explore');
  await expect(page.getByRole('heading', { name: 'Substrate warehouse' })).toBeVisible();
  await expect(page.getByPlaceholder('word, ILI, frame, or id hex…')).toBeVisible();
});

test('Explore resolve navigates to entity preview', async ({ page }) => {
  await page.goto('/explore');
  await page.getByPlaceholder('word, ILI, frame, or id hex…').fill('whale');
  await page.getByRole('button', { name: 'Resolve' }).click();
  await expect(page).toHaveURL(/\/explore\/entity\/[0-9a-f]{32}/i, { timeout: 15_000 });
  await expect(page.getByRole('heading', { level: 2 }).first()).toBeVisible({ timeout: 15_000 });
});

test('Glome canvas mounts after unlock', async ({ page }) => {
  await page.goto('/explore/resolve/whale');
  await expect(page.getByRole('heading', { level: 2 }).first()).toBeVisible({ timeout: 15_000 });
  await page.getByRole('button', { name: /Unlock \(inspect\)/ }).click();
  await page.getByRole('button', { name: 'glome' }).click();
  await page.getByRole('button', { name: /Unlock/ }).first().click();
  await expect(page.locator('canvas')).toBeVisible({ timeout: 15_000 });
});

test('Gated expand shows GatePrompt', async ({ page }) => {
  await page.goto('/explore/resolve/whale');
  await expect(page.getByRole('button', { name: /Unlock \(inspect\)/ })).toBeVisible({ timeout: 15_000 });
  await expect(page.getByText(/inspect/i)).toBeVisible();
});
