import { test, expect } from '@playwright/test';
import { stubApi } from './fixtures/api';

// The Lichess bot is controlled through one routing panel (execution location, read source,
// recording destination). Changing it needs the signed-in account's permission only: no second
// operator token and no HTTPS service URL to type in.
const profile = {
  id: 'desktop', label: 'Desktop', environment: 'development', implementation: 'laplace-uci',
  database: 'laplace', maxConcurrent: 2, rated: false,
};

test('lichess routing has no secondary operator-token or HTTPS roadblock', async ({ page }) => {
  await stubApi(page);
  const selections: { body: Record<string, unknown>; operator?: string }[] = [];

  await page.route((url) => url.pathname.startsWith('/chess/lichess/'), async (route) => {
    const url = new URL(route.request().url());
    if (url.pathname === '/chess/lichess/control') {
      await route.fulfill({ json: {
        available: true, canControl: true,
        control: {
          controller: 'fixture', phase: 'off', activeProfile: null,
          selection: { profile: null, revision: 4, changedUtc: '2026-11-10T00:00:00Z' },
          profiles: [profile],
          bot: { connected: false, username: 'LaplaceBot', gamesRecorded: 0, recentLog: [] },
        },
      } });
      return;
    }
    if (url.pathname === '/chess/lichess/selection' && route.request().method() === 'POST') {
      selections.push({ body: route.request().postDataJSON(), operator: route.request().headers()['x-laplace-operator-token'] });
      await route.fulfill({ status: 202, json: {} });
      return;
    }
    await route.fulfill({ status: 404, json: {} });
  });

  await page.goto('/lab/lichess');
  await expect(page.getByRole('heading', { name: 'Lichess routing' })).toBeVisible({ timeout: 15_000 });
  await expect(page.getByLabel('Operator token', { exact: true })).toHaveCount(0);
  await expect(page.getByText(/HTTPS service URL/i)).toHaveCount(0);

  const location = page.getByRole('combobox', { name: 'Lichess execution location' });
  await expect(location).toBeEnabled();
  await location.selectOption('desktop');
  await page.getByRole('button', { name: 'Apply routing', exact: true }).click();

  await expect.poll(() => selections.length).toBe(1);
  expect(selections[0].body).toMatchObject({ profile: 'desktop', expectedRevision: 4 });
  expect(selections[0].operator).toBeUndefined();
});
