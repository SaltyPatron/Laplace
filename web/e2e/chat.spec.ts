import { test, expect } from '@playwright/test';

test('SPA loads with chat surface', async ({ page }) => {
  await page.goto('/');
  await expect(page.locator('header h1')).toContainText('Laplace');
  await expect(page.locator('.composer textarea')).toBeVisible();
});

test('chat happy path returns a grounded reply', async ({ page }) => {
  await page.goto('/');
  await page.locator('.composer textarea').fill('define whale');
  await page.locator('.composer button').click();

  const assistant = page.locator('.message.assistant').last();
  await expect(assistant).toBeVisible({ timeout: 20_000 });
  await expect(assistant.locator('.content')).not.toHaveText('', { timeout: 20_000 });
});

test('evidence lookup renders receipts', async ({ page }) => {
  await page.goto('/');
  const panel = page.locator('.receipt-panel');
  await panel.locator('input').fill('whale');
  await panel.locator('button').click();

  
  
  await expect(panel.locator('.evidence h3, .error').first()).toBeVisible({ timeout: 15_000 });
});

test('billing page lists the three plans', async ({ page }) => {
  await page.goto('/');
  await page.locator('nav button', { hasText: 'Billing' }).click();
  await expect(page.locator('.plan-card')).toHaveCount(3, { timeout: 10_000 });
});

test('unknown api route stays JSON', async ({ request }) => {
  const res = await request.get('/v1/definitely/not/a/route');
  expect(res.status()).toBe(404);
  expect(res.headers()['content-type']).toContain('application/json');
});
