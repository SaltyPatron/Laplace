import { expect, test } from '@playwright/test';

// These exercise the production components with explicit transport fixtures.
// Endpoint and PostgreSQL tests prove authorization independently of the UI.
const fixture = '/tests/admin-quality.html';

test('administrator finds a person, reviews permissions, and sees the saved history', async ({ page }) => {
  await page.goto(fixture);
  await page.getByRole('button', { name: 'People & roles', exact: true }).click();
  await page.getByRole('searchbox', { name: 'Search people by email or name' }).fill('manager@example.test');
  await page.getByRole('button', { name: 'Search people', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Change role', exact: true })).toHaveCount(1);
  await page.getByRole('button', { name: 'Change role', exact: true }).click();
  await page.getByRole('combobox', { name: 'Platform role', exact: true }).selectOption('observer');
  await expect(page.getByRole('dialog')).toContainText('View ingestion and system activity');
  await page.getByRole('button', { name: 'Save access', exact: true }).click();
  await expect(page.getByRole('status', { name: 'Submitted changes' })).toHaveText('1');
  await expect(page.getByRole('region', { name: 'Recent access changes' })).toContainText('Operations manager → Observer');
});

test('refused access changes remain actionable without reporting success', async ({ page }) => {
  await page.goto(`${fixture}?reject=1&page=${encodeURIComponent('/operator?section=access')}`);
  await page.getByRole('button', { name: 'Change role', exact: true }).first().click();
  await page.getByRole('combobox', { name: 'Platform role', exact: true }).selectOption('');
  await page.getByRole('button', { name: 'Save access', exact: true }).click();
  await expect(page.getByRole('dialog')).toContainText('Keep at least one platform administrator');
  await expect(page.getByRole('status', { name: 'Submitted changes' })).toHaveText('0');
});

test('ingestion reviews the server destination before one launch', async ({ page }) => {
  await page.goto(`${fixture}?page=${encodeURIComponent('/operator?section=ingest')}`);
  await page.getByRole('combobox', { name: '1. Source', exact: true }).selectOption('document');
  await page.getByRole('textbox', { name: /2. Server file or folder/ }).fill('/vault/example-documents');
  await page.getByRole('button', { name: 'Review run', exact: true }).click();
  await expect(page.getByRole('dialog')).toContainText('fixture-server');
  await expect(page.getByRole('dialog')).toContainText('Shared substrate');
  await expect(page.getByRole('status', { name: 'Submitted changes' })).toHaveText('0');
  await page.getByRole('button', { name: 'Start run', exact: true }).click();
  await expect(page.getByText(/Launched Documents/)).toBeVisible();
  await expect(page.getByRole('status', { name: 'Submitted changes' })).toHaveText('1');
  await page.getByRole('button', { name: 'Stop job…', exact: true }).click();
  await expect(page.getByRole('dialog')).toContainText('Data already committed remains available');
  await page.getByRole('button', { name: 'Keep running', exact: true }).click();
  await expect(page.getByRole('status', { name: 'Submitted changes' })).toHaveText('1');
});

test('observer sees journal and services without mutation controls', async ({ page }) => {
  await page.goto(`${fixture}?role=observer&page=${encodeURIComponent('/operator?section=ingest')}`);
  await expect(page.getByRole('heading', { name: 'Ingestion runs', exact: true })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'New ingestion run' })).toHaveCount(0);
  await page.getByRole('button', { name: 'Services', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Refresh MCP' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Start MCP…' })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'People & roles', exact: true })).toHaveCount(0);
});

test('revocation removes a previously open administrative tool', async ({ page }) => {
  await page.goto(`${fixture}?page=${encodeURIComponent('/operator?section=access')}`);
  await expect(page.getByRole('heading', { name: 'Find a person' })).toBeVisible();
  await page.getByRole('button', { name: 'Revoke platform role' }).click();
  await page.getByRole('button', { name: 'Refresh access' }).click();
  await expect(page.getByRole('heading', { name: 'Access needed' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Change role' })).toHaveCount(0);
});

test('failed access reads provide retry instead of claiming the user has no role', async ({ page }) => {
  await page.goto(`${fixture}?accessError=1`);
  await expect(page.getByRole('alert')).toContainText('Access service unavailable');
  await expect(page.getByText('Your account has no platform role.', { exact: false })).toHaveCount(0);
  await page.getByRole('button', { name: 'Restore access read' }).click();
  await page.getByRole('button', { name: 'Retry Your access' }).click();
  await expect(page.getByRole('heading', { name: 'Platform tools' })).toBeVisible();
});

test('phone navigation reaches services without horizontal page overflow', async ({ page }) => {
  await page.setViewportSize({ width: 375, height: 812 });
  await page.goto(fixture);
  await page.getByRole('combobox', { name: 'Administration page', exact: true }).selectOption('services');
  await expect(page.getByRole('heading', { name: 'MCP', exact: true })).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
});
