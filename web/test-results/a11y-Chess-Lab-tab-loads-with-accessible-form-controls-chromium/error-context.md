# Instructions

- Following Playwright test failed.
- Explain why, be concise, respect Playwright best practices.
- Provide a snippet of code with the fix, if possible.

# Test info

- Name: a11y.spec.ts >> Chess Lab tab loads with accessible form controls
- Location: e2e\a11y.spec.ts:12:1

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
  4  | test('App header has no critical a11y violations', async ({ page }) => {
  5  |   await page.goto('/');
  6  |   const results = await new AxeBuilder({ page })
  7  |     .include('header')
  8  |     .analyze();
  9  |   expect(results.violations.filter((v) => v.impact === 'critical' || v.impact === 'serious')).toEqual([]);
  10 | });
  11 | 
  12 | test('Chess Lab tab loads with accessible form controls', async ({ page }) => {
> 13 |   await page.goto('/');
     |              ^ Error: page.goto: net::ERR_CONNECTION_REFUSED at http://127.0.0.1:5187/
  14 |   await page.getByRole('button', { name: 'Lab' }).click();
  15 |   await expect(page.getByRole('heading', { name: 'Chess Lab' })).toBeVisible();
  16 |   const results = await new AxeBuilder({ page })
  17 |     .include('main')
  18 |     .analyze();
  19 |   expect(results.violations.filter((v) => v.impact === 'critical' || v.impact === 'serious')).toEqual([]);
  20 | });
  21 | 
```