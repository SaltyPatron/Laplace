# Instructions

- Following Playwright test failed.
- Explain why, be concise, respect Playwright best practices.
- Provide a snippet of code with the fix, if possible.

# Test info

- Name: chat.spec.ts >> SPA loads with chat surface
- Location: e2e\chat.spec.ts:3:1

# Error details

```
Error: page.goto: net::ERR_CONNECTION_REFUSED at http://127.0.0.1:5187/
Call log:
  - navigating to "http://127.0.0.1:5187/", waiting until "load"

```

# Test source

```ts
  1  | import { test, expect } from '@playwright/test';
  2  | 
  3  | test('SPA loads with chat surface', async ({ page }) => {
> 4  |   await page.goto('/');
     |              ^ Error: page.goto: net::ERR_CONNECTION_REFUSED at http://127.0.0.1:5187/
  5  |   await expect(page.locator('header h1')).toContainText('Laplace');
  6  |   await expect(page.getByPlaceholder('Ask about anything the witnesses attest to…')).toBeVisible();
  7  | });
  8  | 
  9  | test('chat happy path returns a grounded reply', async ({ page }) => {
  10 |   await page.goto('/');
  11 |   await page.getByPlaceholder('Ask about anything the witnesses attest to…').fill('define whale');
  12 |   await page.getByRole('button', { name: 'Send' }).click();
  13 | 
  14 |   await expect(page.getByText('define whale')).toBeVisible();
  15 |   await expect(page.getByRole('button', { name: 'Send' })).toBeEnabled({ timeout: 20_000 });
  16 |   await expect(page.locator('main')).not.toHaveText(/^[\s…]*$/);
  17 | });
  18 | 
  19 | test('evidence lookup renders receipts', async ({ page }) => {
  20 |   await page.goto('/');
  21 |   const panel = page.getByRole('heading', { name: 'Evidence' }).locator('..');
  22 |   await panel.getByPlaceholder('word or entity id').fill('whale');
  23 |   await panel.getByRole('button', { name: 'Look up' }).click();
  24 | 
  25 |   await expect(
  26 |     panel.getByRole('heading', { level: 3 }).or(panel.locator('[role="alert"]')).first(),
  27 |   ).toBeVisible({ timeout: 15_000 });
  28 | });
  29 | 
  30 | test('billing page lists the three plans', async ({ page }) => {
  31 |   await page.goto('/');
  32 |   await page.getByRole('button', { name: 'Billing' }).click();
  33 |   await expect(page.getByRole('heading', { name: 'Plans' })).toBeVisible();
  34 |   await expect(page.getByRole('button', { name: 'Subscribe' })).toHaveCount(3, { timeout: 10_000 });
  35 | });
  36 | 
  37 | test('unknown api route stays JSON', async ({ request }) => {
  38 |   const res = await request.get('/v1/definitely/not/a/route');
  39 |   expect(res.status()).toBe(404);
  40 |   expect(res.headers()['content-type']).toContain('application/json');
  41 | });
  42 | 
```