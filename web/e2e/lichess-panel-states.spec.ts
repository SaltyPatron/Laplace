import { test, expect, type Page } from '@playwright/test';
import { stubApi } from './fixtures/api';

// The Lichess routing panel reports the controller's one state. A crash-looping bot shows its
// error and keeps the requested destination; it never claims the bot is unconfigured. A viewer
// without permission sees the configuration but cannot change it.

const profile = {
  id: 'desktop', label: 'Desktop', environment: 'development', implementation: 'laplace-uci',
  database: 'laplace', maxConcurrent: 2, rated: false,
};
const control = (phase: string, extra: Record<string, unknown> = {}) => ({
  controller: 'fixture', phase, activeProfile: null,
  selection: { profile: 'desktop', revision: 7, changedUtc: '2026-11-10T00:00:00Z' },
  profiles: [profile],
  bot: { connected: false, username: 'SaltyPatron', gamesRecorded: 0, recentLog: [] },
  ...extra,
});

async function serve(page: Page, next: () => object) {
  await stubApi(page);
  await page.route((url) => url.pathname.startsWith('/chess/lichess/'), async (route) => {
    const path = new URL(route.request().url()).pathname;
    if (path === '/chess/lichess/control') await route.fulfill({ json: next() });
    else await route.fulfill({ status: 404, json: {} });
  });
}

test('a crash loop keeps the requested destination and never claims the bot is unconfigured', async ({ page }) => {
  let i = 0;
  await serve(page, () => ({
    available: true, canControl: true,
    control: i++ % 2 === 0
      ? control('restarting', { error: 'The managed Lichess service exited and is restarting. Last error: No password has been provided' })
      : control('failed', { error: 'No password has been provided' }),
  }));
  await page.goto('/lab/lichess');
  const location = page.getByRole('combobox', { name: 'Lichess execution location' });
  await expect(location).toHaveValue('desktop', { timeout: 15_000 });
  for (let k = 0; k < 3; k++) {
    await page.evaluate(() => document.dispatchEvent(new Event('visibilitychange')));
    await expect(location).toHaveValue('desktop');
    await expect(page.getByText(/State: (restarting|failed)/)).toBeVisible();
    await expect(page.getByText(/second bot|not configured/i)).toHaveCount(0);
    await expect(page.getByText(/No password has been provided/).first()).toBeVisible();
  }
});

test('a viewer without permission sees the routing but cannot change it', async ({ page }) => {
  await serve(page, () => ({ available: true, canControl: false, control: control('listening', { activeProfile: 'desktop' }) }));
  await page.goto('/lab/lichess');
  const location = page.getByRole('combobox', { name: 'Lichess execution location' });
  await expect(location).toHaveValue('desktop', { timeout: 15_000 });
  await expect(location).toBeDisabled();
  await expect(page.getByRole('button', { name: 'Apply routing', exact: true })).toBeDisabled();
  await expect(page.getByText('does not have permission to change the bot destination', { exact: false })).toBeVisible();
  await expect(page.getByText(/State: listening/)).toBeVisible();
});
