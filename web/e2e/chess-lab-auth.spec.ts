import { test, expect } from '@playwright/test';

test('lichess lab has no secondary operator-token or HTTPS roadblock', async ({ page }) => {
  const startOperatorHeaders: (string | undefined)[] = [];

  await page.route('**/chess/lichess/**', async (route) => {
    const url = new URL(route.request().url());
    if (url.pathname === '/chess/lichess/status') {
      await route.fulfill({
        contentType: 'application/json',
        body: JSON.stringify({
          configured: true,
          tokenPreview: 'lichess-server-secret',
          connected: false,
          running: false,
          username: 'LaplaceBot',
          depth: 6,
          maxConcurrent: 2,
          substrate: true,
          gamesRecorded: 0,
          recentLog: [],
        }),
      });
      return;
    }

    if (url.pathname === '/chess/lichess/start' && route.request().method() === 'POST') {
      startOperatorHeaders.push(route.request().headers()['x-laplace-operator-token']);
      await route.fulfill({
        status: 202,
        contentType: 'application/json',
        body: JSON.stringify({ accepted: true }),
      });
      return;
    }

    await route.fulfill({ status: 404, body: '' });
  });

  await page.goto('/lab/lichess');
  await expect(page.getByLabel('Operator token', { exact: true })).toHaveCount(0);
  await expect(page.getByText(/HTTPS service URL/i)).toHaveCount(0);

  const listen = page.getByRole('switch', { name: 'Listen on Lichess' });
  await expect(listen).toBeEnabled();
  await listen.click();

  await expect.poll(() => startOperatorHeaders.length).toBe(1);
  expect(startOperatorHeaders[0]).toBeUndefined();
});
