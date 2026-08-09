import { test, expect } from '@playwright/test';

test.describe('chess UI', () => {
  test('play tab loads and shows board', async ({ page }) => {
    await page.goto('/');
    const sessionPromise = page.waitForResponse((response) =>
      response.url().endsWith('/chess/play/start') && response.request().method() === 'POST');
    await page.getByRole('button', { name: 'Play', exact: true }).click();
    const sessionResponse = await sessionPromise;
    expect(sessionResponse.ok(), await sessionResponse.text()).toBe(true);
    await expect(page.getByRole('button', { name: 'New game' })).toBeVisible();
    await expect(page.getByRole('grid')).toBeVisible();
  });

  test('lab tab completes a read-only job', async ({ page }) => {
    await page.goto('/');
    await page.getByRole('button', { name: 'Lab', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'Chess Lab' })).toBeVisible();
    await page.getByRole('option', { name: /Learned PST grid/ }).click();

    const responsePromise = page.waitForResponse((response) =>
      response.url().endsWith('/chess/lab/start') && response.request().method() === 'POST');
    await page.getByRole('button', { name: 'Start experiment', exact: true }).click();
    const response = await responsePromise;
    expect(response.ok(), await response.text()).toBe(true);

    await expect(page.getByText('Completed', { exact: true }).first()).toBeVisible({ timeout: 15_000 });
  });
});
