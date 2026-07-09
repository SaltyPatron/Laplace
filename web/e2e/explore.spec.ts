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
  // Under the dev billing bypass the entity auto-unlocks (no "Unlock (inspect)" step);
  // the glome tab still gates the nearest-neighbor overlay behind its own prompt.
  await page.getByRole('button', { name: 'glome' }).click();
  await page.getByRole('button', { name: /Unlock \(nn\)/ }).click();
  await expect(page.locator('canvas')).toBeVisible({ timeout: 15_000 });
});

test('Gated expand shows GatePrompt when billing bypass is off', async ({ page }) => {
  // The inspect gate only fires when the endpoint runs with LAPLACE_BILLING_BYPASS=false;
  // under the dev bypass the entity auto-unlocks and this UX is unreachable by design.
  test.skip(process.env.LAPLACE_BILLING_BYPASS !== 'false', 'requires an endpoint with LAPLACE_BILLING_BYPASS=false');
  await page.goto('/explore/resolve/whale');
  await expect(page.getByRole('button', { name: /Unlock \(inspect\)/ })).toBeVisible({ timeout: 15_000 });
  await expect(page.getByText(/inspect/i)).toBeVisible();
});
