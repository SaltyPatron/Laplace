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

test('workspace removal requires review and preserves the member when refused', async ({ page }) => {
  await page.goto(`${fixture}?reject=1&page=${encodeURIComponent('/settings?section=members')}`);
  await page.getByRole('searchbox', { name: 'Find a member', exact: true }).fill('manager@example.test');
  await page.getByRole('button', { name: 'Remove', exact: true }).click();
  await expect(page.getByRole('dialog')).toContainText('manager@example.test');
  await expect(page.getByRole('dialog')).toContainText('Laplace team');
  await page.getByRole('button', { name: 'Cancel', exact: true }).click();
  await expect(page.getByRole('status', { name: 'Submitted changes' })).toHaveText('0');
  await page.getByRole('button', { name: 'Remove', exact: true }).click();
  await page.getByRole('button', { name: 'Remove member', exact: true }).click();
  await expect(page.getByRole('dialog')).toContainText('Your permissions changed');
  await expect(page.getByRole('status', { name: 'Submitted changes' })).toHaveText('0');
});

test('named API keys remain available to save and revocation has visible results', async ({ page }) => {
  await page.goto(`${fixture}?page=${encodeURIComponent('/settings?section=clients')}`);
  await expect(page.getByRole('button', { name: 'Create API key', exact: true })).toBeDisabled();
  await page.getByRole('textbox', { name: 'Application name', exact: true }).fill('Research importer');
  await page.getByRole('button', { name: 'Create API key', exact: true }).click();
  await expect(page.getByLabel('Save this key now', { exact: true })).toHaveAttribute('type', 'password');
  await page.getByRole('button', { name: 'Show key', exact: true }).click();
  await expect(page.getByLabel('Save this key now', { exact: true })).toHaveValue('lp_fixture_1_not_a_real_secret');
  await page.getByRole('textbox', { name: 'Application name', exact: true }).fill('Another importer');
  await expect(page.getByRole('button', { name: 'Create API key', exact: true })).toBeDisabled();
  await page.getByRole('searchbox', { name: 'Find an API client', exact: true }).fill('Analytics integration');
  await page.getByRole('button', { name: 'Revoke key', exact: true }).click();
  await page.getByRole('dialog').getByRole('button', { name: 'Revoke key', exact: true }).click();
  await expect(page.getByLabel('Save this key now', { exact: true })).toHaveValue('lp_fixture_1_not_a_real_secret');
  await page.getByRole('button', { name: 'I have saved this key', exact: true }).click();
  await page.getByRole('searchbox', { name: 'Find an API client', exact: true }).fill('Research importer');
  await page.getByRole('button', { name: 'Revoke key', exact: true }).click();
  await expect(page.getByRole('dialog')).toContainText('Research importer');
  await page.getByRole('dialog').getByRole('button', { name: 'Revoke key', exact: true }).click();
  await expect(page.getByText('No API clients match these filters.', { exact: true })).toBeVisible();
  await page.getByRole('checkbox', { name: 'Include revoked keys' }).check();
  await expect(page.getByRole('region', { name: 'API clients', exact: true })).toContainText('Research importer');
  await expect(page.getByRole('button', { name: 'Revoke key', exact: true })).toHaveCount(0);
});

test('invitation names the destination and retries selection without accepting twice', async ({ page }) => {
  await page.goto(`${fixture}?page=${encodeURIComponent(`/settings#join=${'a'.repeat(64)}`)}`);
  await expect(page.getByRole('region', { name: 'Workspace invitation' })).toContainText('Research team');
  await expect(page.getByRole('region', { name: 'Workspace invitation' })).toContainText('Member');
  await expect(page.getByRole('status', { name: 'Submitted changes' })).toHaveText('0');
  await page.getByRole('button', { name: 'Accept invitation', exact: true }).click();
  await expect(page.getByText(/You joined the workspace, but switching to it failed/)).toBeVisible();
  await expect(page.getByRole('combobox', { name: 'Current workspace', exact: true }).first()).toContainText('Research team');
  await page.getByRole('button', { name: 'Open workspace', exact: true }).click();
  await expect(page.getByRole('status', { name: 'Submitted changes' })).toHaveText('1');
});

test('workspace creation retries switching without creating a duplicate workspace', async ({ page }) => {
  await page.goto(`${fixture}?page=${encodeURIComponent('/settings')}`);
  await page.getByRole('textbox', { name: 'New company workspace', exact: true }).fill('Client research');
  await page.getByRole('button', { name: 'Create workspace', exact: true }).click();
  await expect(page.getByText(/Workspace "Client research" was created, but switching to it failed/)).toBeVisible();
  await expect(page.getByRole('combobox', { name: 'Current workspace', exact: true }).first()).toContainText('Client research');
  await page.getByRole('button', { name: 'Open created workspace', exact: true }).click();
  await expect(page.getByRole('status', { name: 'Submitted changes' })).toHaveText('1');
  await expect(page.getByRole('textbox', { name: 'New company workspace', exact: true })).toBeDisabled();
});

test('workspace load failures offer retry and phone settings remain navigable', async ({ page }) => {
  await page.setViewportSize({ width: 375, height: 812 });
  await page.goto(`${fixture}?accountError=1&page=${encodeURIComponent('/settings?section=members')}`);
  await expect(page.getByRole('button', { name: 'Retry workspaces', exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Restore account read', exact: true }).click();
  await page.getByRole('button', { name: 'Retry workspaces', exact: true }).click();
  await expect(page.getByRole('searchbox', { name: 'Find a member' })).toBeVisible();
  await page.getByRole('combobox', { name: 'Settings page', exact: true }).selectOption('clients');
  await expect(page.getByRole('textbox', { name: 'Application name', exact: true })).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
});
