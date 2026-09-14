import { test, expect } from '@playwright/test';

async function expectOk(response: import('@playwright/test').Response) {
  const failureBody = response.ok() ? undefined : await response.text();
  expect(response.ok(), failureBody).toBe(true);
}

test.describe('chess UI', () => {
  test('gauntlet accepts non-default Elo without a second operator credential', async ({ page }) => {
    const previews: URL[] = [];
    const starts: { config: Record<string, string> }[] = [];
    const startOperatorHeaders: (string | undefined)[] = [];
    // All chess requests are mocked: this UI contract must not start an engine or write a game.
    await page.route('**/chess/lab/**', async (route) => {
      const url = new URL(route.request().url());
      if (!url.pathname.startsWith('/chess/lab/')) {
        await route.fallback();
        return;
      }
      let body: unknown = [];
      if (url.pathname.endsWith('/catalog')) {
        body = { engines: Object.fromEntries(['cutechess', 'stockfish', 'qt', 'laplaceUci']
          .map((key) => [key, { path: `/test/${key}`, found: true, source: 'config' }])) };
      } else if (url.pathname.endsWith('/preview')) {
        previews.push(url);
        body = { commandLine: 'cutechess-cli -debug all', ready: true, games: 10, missing: [] };
      } else if (url.pathname.endsWith('/start')) {
        startOperatorHeaders.push(route.request().headers()['x-laplace-operator-token']);
        starts.push(route.request().postDataJSON());
        body = { jobId: 'ui-only-test' };
      } else if (url.pathname.includes('/events')) {
        await route.fulfill({ contentType: 'text/event-stream', body: '' });
        return;
      }
      await route.fulfill({ contentType: 'application/json', body: JSON.stringify(body) });
    });

    await page.goto('/lab/gauntlet');
    await expect(page.getByLabel('Operator token', { exact: true })).toHaveCount(0);
    await expect(page.getByText('Operator access', { exact: true })).toHaveCount(0);
    const startButton = page.getByRole('button', { name: 'Start gauntlet', exact: true });
    const limitStrength = page.getByLabel('Limit Stockfish by UCI Elo', { exact: true });
    await expect(startButton).toBeEnabled();
    await expect(limitStrength).toBeChecked();

    const elo = page.getByRole('spinbutton', { name: 'Stockfish Elo cap' });
    await expect(elo).toHaveValue('2000');
    await elo.fill('2300');
    await expect(elo).toHaveValue('2300');
    await expect.poll(() => previews.at(-1)?.searchParams.get('elo')).toBe('2300');
    await startButton.click();
    await expect.poll(() => starts.length).toBe(1);
    expect(starts[0].config).toMatchObject({ elo: '2300', limitStrength: 'true' });
    expect(startOperatorHeaders[0]).toBeUndefined();

    await limitStrength.click();
    await expect(limitStrength).not.toBeChecked();
    await expect(elo).toBeDisabled();
    await expect.poll(() => previews.at(-1)?.searchParams.get('limitStrength')).toBe('false');
    await startButton.click();
    await expect.poll(() => starts.length).toBe(2);
    expect(starts[1].config).toMatchObject({ elo: '2300', limitStrength: 'false' });
    expect(startOperatorHeaders[1]).toBeUndefined();

    await limitStrength.click();
    await expect(limitStrength).toBeChecked();
    await expect(elo).toBeEnabled();
    await expect(elo).toHaveValue('2300');
  });

  test('player search, sorting, and paging are URL-addressable', async ({ page }) => {
    const requests: URL[] = [];
    await page.route('**/v1/chess/players?**', async (route) => {
      requests.push(new URL(route.request().url()));
      await route.fulfill({
        contentType: 'application/json',
        body: JSON.stringify({
          object: 'chess.players',
          total: 101,
          offset: Number(new URL(route.request().url()).searchParams.get('offset') ?? 0),
          players: [{
            rank: 51,
            id: '11112222333344445555666677778888',
            name: 'Karpov, Anatoly',
            games: 2056,
            rating: 1820,
            rd: 60,
            eff_mu: 1700,
          }],
        }),
      });
    });

    await page.goto('/chess?q=Karpov&sort=games&direction=desc&offset=50');
    await expect(page.getByRole('link', { name: 'Karpov, Anatoly' })).toBeVisible();
    await expect(page.getByRole('textbox', { name: 'Find a chess player' })).toHaveValue('Karpov');
    await expect.poll(() => requests.at(-1)?.searchParams.toString()).toContain('search=Karpov');
    expect(requests.at(-1)?.searchParams.get('sort')).toBe('games');
    expect(requests.at(-1)?.searchParams.get('direction')).toBe('desc');
    expect(requests.at(-1)?.searchParams.get('offset')).toBe('50');

    await page.getByRole('button', { name: /Rating/ }).click();
    await expect(page).toHaveURL(/q=Karpov/);
    await expect(page).toHaveURL(/sort=rating/);
    await expect(page).toHaveURL(/direction=asc/);
    await expect(page.getByRole('button', { name: /Rating/ })).toContainText('↑');

    await page.getByRole('button', { name: 'Next' }).click();
    await expect(page).toHaveURL(/offset=100/);
    expect(requests.at(-1)?.searchParams.get('offset')).toBe('100');
  });
});
