import { test, expect } from '@playwright/test';

test.describe('chess UI', () => {
  test('play tab loads and shows board', async ({ page }) => {
    await page.goto('/');
    await page.getByRole('button', { name: 'Play' }).click();
    await expect(page.getByRole('button', { name: 'New game' })).toBeVisible();
    await expect(page.getByRole('grid')).toBeVisible();
  });

  test('lab tab starts and stops a short job', async ({ page }) => {
    await page.goto('/');
    await page.getByRole('button', { name: 'Lab' }).click();
    await expect(page.getByRole('heading', { name: 'Chess Lab' })).toBeVisible();
    await page.getByRole('button', { name: 'Start' }).click();
    await page.waitForTimeout(2000);
    await page.getByRole('button', { name: 'Stop' }).click();
  });
});
