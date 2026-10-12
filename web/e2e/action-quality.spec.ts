import { expect, test } from '@playwright/test';

test.skip(process.env.LAPLACE_E2E_FIXTURES !== '1', 'requires the Vite-only interaction fixtures');

test('an approved quote does not claim that billing was bypassed', async ({ page }) => {
  await page.goto('/tests/action-quality.html?view=gate-paid');
  await page.getByRole('button', { name: 'Unlock (fixture.read)' }).click();
  await expect(page.getByLabel('Ready calls')).toHaveText('1');
  await expect(page.getByText('2.50 usd — status approved')).toBeVisible();
  await expect(page.getByText(/Billing bypass active/)).toHaveCount(0);
});

test('only server-confirmed bypass automatically opens the read', async ({ page }) => {
  await page.goto('/tests/action-quality.html?view=gate-bypass');
  await expect(page.getByLabel('Ready calls')).toHaveText('1');
  await expect(page.getByText('Billing bypass active — no charge applied.')).toBeVisible();
});

test('a quote finishing after a workspace switch cannot open the old read', async ({ page }) => {
  await page.goto('/tests/action-quality.html?view=gate-paid&deferQuote=1');
  await page.getByRole('button', { name: 'Unlock (fixture.read)' }).click();
  await expect(page.getByRole('button', { name: 'Preflight…' })).toBeDisabled();
  await page.getByRole('button', { name: 'Switch workspace' }).click();
  await page.getByRole('button', { name: 'Complete pending quote' }).click();
  await expect(page.getByLabel('Ready calls')).toHaveText('0');
  await expect(page.getByText('2.50 usd — status approved')).toHaveCount(0);
});

test('training surfaces lost command responses and recovers by reading actual status', async ({ page }) => {
  await page.goto('/tests/action-quality.html?view=training');
  await page.getByRole('button', { name: 'Run 200', exact: true }).click();
  await expect(page.getByRole('alert').filter({ hasText: 'Start request failed' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Run 200', exact: true })).toBeDisabled();
  await page.getByRole('button', { name: 'Refresh status' }).click();
  await page.getByRole('button', { name: 'Stop', exact: true }).click();
  await expect(page.getByRole('alert').filter({ hasText: 'Stop request failed' })).toBeVisible();
  await page.getByRole('button', { name: 'Refresh status' }).click();
  await expect(page.getByRole('button', { name: 'Run 200', exact: true })).toBeEnabled();
});

test('training status failures offer a retry and prevent an unconfirmed start', async ({ page }) => {
  await page.goto('/tests/action-quality.html?view=training&statusError=1');
  await expect(page.getByRole('alert').filter({ hasText: 'Cannot read training status' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Run 200', exact: true })).toBeDisabled();
  await page.getByRole('button', { name: 'Restore status reads' }).click();
  await page.getByRole('button', { name: 'Refresh status' }).click();
  await expect(page.getByRole('button', { name: 'Run 200', exact: true })).toBeEnabled();
});

test('a status read started before stopping cannot restore a stale running state', async ({ page }) => {
  await page.goto('/tests/action-quality.html?view=training&running=1');
  await page.getByRole('button', { name: 'Allow commands' }).click();
  await page.getByRole('button', { name: 'Delay next status read' }).click();
  await page.getByRole('button', { name: 'Refresh status' }).click();
  await page.getByRole('button', { name: 'Stop', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Run 200', exact: true })).toBeEnabled();
  await page.getByRole('button', { name: 'Complete pending status' }).click();
  await expect(page.getByRole('button', { name: 'Run 200', exact: true })).toBeEnabled();
  await expect(page.getByRole('button', { name: 'Stop', exact: true })).toBeDisabled();
});
