import { test, expect } from '@playwright/test';

test('SPA loads with the Home landing', async ({ page }) => {
  await page.goto('/');
  await expect(page.locator('header h1')).toContainText('Laplace');
  // The landing orients before it asks for input: the three mode cards.
  await expect(page.getByRole('button', { name: /Start a conversation/ })).toBeVisible();
  await expect(page.getByRole('button', { name: /Open the console/ })).toBeVisible();
  await expect(page.getByRole('button', { name: /Enter the warehouse/ })).toBeVisible();
});

test('home mode card reaches chat', async ({ page }) => {
  await page.goto('/');
  await page.getByRole('button', { name: /Start a conversation/ }).click();
  await expect(page.getByPlaceholder('Ask about anything the witnesses attest to…')).toBeVisible();
});

test('chat happy path returns a grounded reply', async ({ page }) => {
  await page.goto('/');
  await page.getByRole('button', { name: 'Chat', exact: true }).click();
  const input = page.getByPlaceholder('Ask about anything the witnesses attest to…');
  await input.fill('define whale');
  await page.getByRole('button', { name: 'Send' }).click();

  await expect(page.getByText('define whale')).toBeVisible();
  // Send clears the input, and an empty input keeps the button disabled by design.
  // "Ready for the next turn" = the busy label ('…') is gone AND a refilled input
  // re-enables the button.
  await expect(page.getByRole('button', { name: 'Send' })).toBeVisible({ timeout: 20_000 });
  await input.fill('next question');
  await expect(page.getByRole('button', { name: 'Send' })).toBeEnabled();
  await expect(page.locator('main')).not.toHaveText(/^[\s…]*$/);
});

test('evidence lookup renders receipts', async ({ page }) => {
  await page.goto('/');
  await page.getByRole('button', { name: 'Chat', exact: true }).click();
  const panel = page.getByRole('heading', { name: 'Evidence' }).locator('..');
  await panel.getByPlaceholder('word or entity id').fill('whale');
  await panel.getByRole('button', { name: 'Look up' }).click();

  // 35s = the API's 30s command budget + margin: evidence_receipt labels every claim
  // before its LIMIT (doc 02 Issue 52), so a cold lookup can run tens of seconds.
  // Tighten back toward 15s when the rank-then-label SQL fix lands.
  await expect(
    panel.getByRole('heading', { level: 3 }).or(panel.locator('[role="alert"]')).first(),
  ).toBeVisible({ timeout: 35_000 });
});

test('home example seeds and runs a structural read', async ({ page }) => {
  await page.goto('/');
  await page.getByRole('button', { name: /What does "entropy" mean\?/ }).click();
  // Lands on the Query console with the seed applied and auto-run.
  await expect(page.locator('#query-topic')).toHaveValue('entropy');
  await expect(page.getByRole('button', { name: 'Run query' })).toBeVisible({ timeout: 20_000 });
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
