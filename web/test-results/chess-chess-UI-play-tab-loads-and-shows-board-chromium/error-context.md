# Instructions

- Following Playwright test failed.
- Explain why, be concise, respect Playwright best practices.
- Provide a snippet of code with the fix, if possible.

# Test info

- Name: chess.spec.ts >> chess UI >> play tab loads and shows board
- Location: e2e\chess.spec.ts:4:3

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
  3  | test.describe('chess UI', () => {
  4  |   test('play tab loads and shows board', async ({ page }) => {
> 5  |     await page.goto('/');
     |                ^ Error: page.goto: net::ERR_CONNECTION_REFUSED at http://127.0.0.1:5187/
  6  |     await page.getByRole('button', { name: 'Play' }).click();
  7  |     await expect(page.getByRole('button', { name: 'New game' })).toBeVisible();
  8  |     await expect(page.getByRole('grid')).toBeVisible();
  9  |   });
  10 | 
  11 |   test('lab tab starts and stops a short job', async ({ page }) => {
  12 |     await page.goto('/');
  13 |     await page.getByRole('button', { name: 'Lab' }).click();
  14 |     await expect(page.getByRole('heading', { name: 'Chess Lab' })).toBeVisible();
  15 |     await page.getByRole('button', { name: 'Start' }).click();
  16 |     await page.waitForTimeout(2000);
  17 |     await page.getByRole('button', { name: 'Stop' }).click();
  18 |   });
  19 | });
  20 | 
```