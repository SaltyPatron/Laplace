import { test, expect } from '@playwright/test';

test('SPA loads with chat surface', async ({ page }) => {
  await page.goto('/');
  await expect(page.locator('header h1')).toContainText('Laplace');
  await expect(page.getByPlaceholder('Ask about anything the witnesses attest to…')).toBeVisible();
});

test('chat happy path returns a grounded reply', async ({ page }) => {
  await page.goto('/');
  await page.getByPlaceholder('Ask about anything the witnesses attest to…').fill('define whale');
  await page.getByRole('button', { name: 'Send' }).click();

  await expect(page.getByText('define whale')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Send' })).toBeEnabled({ timeout: 20_000 });
  await expect(page.locator('main')).not.toHaveText(/^[\s…]*$/);
});

test('evidence lookup renders receipts', async ({ page }) => {
  await page.goto('/');
  const panel = page.getByRole('heading', { name: 'Evidence' }).locator('..');
  await panel.getByPlaceholder('word or entity id').fill('whale');
  await panel.getByRole('button', { name: 'Look up' }).click();

  await expect(
    panel.getByRole('heading', { level: 3 }).or(panel.locator('[role="alert"]')).first(),
  ).toBeVisible({ timeout: 15_000 });
});

test('billing page lists the three plans', async ({ page }) => {
  await page.goto('/');
  await page.getByRole('button', { name: 'Billing' }).click();
  await expect(page.getByRole('heading', { name: 'Plans' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Subscribe' })).toHaveCount(3, { timeout: 10_000 });
});

test('unknown api route stays JSON', async ({ request }) => {
  const res = await request.get('/v1/definitely/not/a/route');
  expect(res.status()).toBe(404);
  expect(res.headers()['content-type']).toContain('application/json');
});
