# Instructions

- Following Playwright test failed.
- Explain why, be concise, respect Playwright best practices.
- Provide a snippet of code with the fix, if possible.

# Test info

- Name: ui-a11y.spec.ts >> UI pilot a11y >> chess lab view loads
- Location: e2e\ui-a11y.spec.ts:13:3

# Error details

```
Error: page.goto: net::ERR_CONNECTION_REFUSED at http://127.0.0.1:5187/
Call log:
  - navigating to "http://127.0.0.1:5187/", waiting until "load"

```

# Test source

```ts
  1  | import { test, expect } from '@playwright/test';
  2  | import AxeBuilder from '@axe-core/playwright';
  3  | 
  4  | test.describe('UI pilot a11y', () => {
  5  |   test('main shell header has no axe violations', async ({ page }) => {
  6  |     await page.goto('/');
  7  |     const results = await new AxeBuilder({ page })
  8  |       .include('header')
  9  |       .analyze();
  10 |     expect(results.violations.filter((v) => v.impact === 'critical' || v.impact === 'serious')).toEqual([]);
  11 |   });
  12 | 
  13 |   test('chess lab view loads', async ({ page }) => {
> 14 |     await page.goto('/');
     |                ^ Error: page.goto: net::ERR_CONNECTION_REFUSED at http://127.0.0.1:5187/
  15 |     await page.getByRole('button', { name: 'Lab' }).click();
  16 |     await expect(page.getByRole('heading', { name: 'Chess Lab' })).toBeVisible();
  17 |   });
  18 | });
  19 | 
```