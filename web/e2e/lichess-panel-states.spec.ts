import { test, expect, type Page } from '@playwright/test';

// The Lichess panel shows the API's one state and never flips between "unconfigured" and "running" while
// the managed service crash-loops, and a start holds the toggle until the service reaches it.

const base = {
  depth: 6, maxConcurrent: 2, substrate: true, gamesRecorded: 0, recentLog: [], connected: false,
  username: 'SaltyPatron', account: null, lastSeenAt: null,
};
const restarting = {
  ...base, state: 'restarting', reachable: false, desired: true, configured: null, running: false,
  error: 'The managed Lichess service (LaplaceLichess) exited and is restarting. Last error: No password has been provided',
  service: { unit: 'LaplaceLichess', loadState: 'loaded', activeState: 'activating', subState: 'auto-restart', enabled: 'enabled', operatorStopped: false },
};
const failed = {
  ...base, state: 'failed', reachable: true, desired: true, configured: true, running: false,
  error: 'No password has been provided', service: null,
};
const stopped = {
  ...base, state: 'stopped', reachable: false, desired: false, configured: null, running: false, error: 'Stopped by an operator.',
  service: { unit: 'LaplaceLichess', loadState: 'loaded', activeState: 'inactive', subState: 'dead', enabled: 'enabled', operatorStopped: true },
};
const listening = { ...base, state: 'listening', reachable: true, desired: true, configured: true, running: true, connected: true, error: null, service: null };

async function serve(page: Page, next: () => object, onStart?: () => void) {
  await page.route('**/chess/lichess/**', async (route) => {
    const path = new URL(route.request().url()).pathname;
    if (path === '/chess/lichess/status') {
      await route.fulfill({ contentType: 'application/json', body: JSON.stringify(next()) });
    } else if (path === '/chess/lichess/start' && route.request().method() === 'POST') {
      onStart?.();
      await route.fulfill({ status: 202, contentType: 'application/json', body: '{}' });
    } else {
      await route.fulfill({ status: 404, body: '' });
    }
  });
}

test('a crash loop keeps the toggle on and never claims the token is unconfigured', async ({ page }) => {
  let i = 0;
  await serve(page, () => (i++ % 2 === 0 ? restarting : failed));
  await page.goto('/lab/lichess');
  const listen = page.getByRole('switch', { name: 'Listen on Lichess' });
  const state = page.getByLabel('Lichess service state');
  for (let k = 0; k < 3; k++) {
    await page.evaluate(() => document.dispatchEvent(new Event('visibilitychange')));
    await expect(listen).toBeChecked();
    await expect(state).toHaveText(/Restarting after failure|Failed/);
    await expect(page.getByText(/second bot|not configured/i)).toHaveCount(0);
    await expect(page.getByText(/No password has been provided/).first()).toBeVisible();
  }
});

test('start holds the toggle until the service is listening', async ({ page }) => {
  let started = false;
  let polls = 0;
  await serve(page, () => {
    if (!started) return stopped;
    polls += 1;
    return polls < 3 ? stopped : listening;
  }, () => { started = true; });
  await page.goto('/lab/lichess');
  const listen = page.getByRole('switch', { name: 'Listen on Lichess' });
  await expect(listen).not.toBeChecked();
  await expect(page.getByText('stopped by an operator', { exact: false })).toBeVisible();
  await listen.click();
  await expect(listen).toBeChecked();
  await expect(page.getByLabel('Lichess service state')).toHaveText(/Starting…|Listening/);
  await expect(page.getByLabel('Lichess service state')).toHaveText(/Listening/, { timeout: 15_000 });
  await expect(listen).toBeChecked();
});
