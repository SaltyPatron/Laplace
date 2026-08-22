import { test, expect } from '@playwright/test';

async function expectOk(response: import('@playwright/test').Response) {
  const failureBody = response.ok() ? undefined : await response.text();
  expect(response.ok(), failureBody).toBe(true);
}

test.describe('chess UI', () => {
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
    await expect(page).toHaveURL(/direction=desc/);
    expect(new URL(page.url()).searchParams.has('offset')).toBe(false);

    await page.getByRole('button', { name: /Rating/ }).click();
    await expect(page).toHaveURL(/direction=asc/);
  });

  test('Laplace games have a dedicated, filterable URL', async ({ page }) => {
    await page.route('**/v1/chess/laplace/games?**', async (route) => {
      await route.fulfill({
        contentType: 'application/json',
        body: JSON.stringify({
          object: 'chess.games',
          player_id: '0123456789abcdef0123456789abcdef',
          offset: 0,
          games: [
            { id: 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', played_on: '2026.08.19', event: 'Browser game', eco: null, as_white: false, opponent_id: 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb', opponent: 'Kasparov', result: '0-1', outcome: 2 },
            { id: 'cccccccccccccccccccccccccccccccc', played_on: '2026.08.18', event: 'Lichess', eco: 'B12', as_white: true, opponent_id: null, opponent: 'Other', result: '1/2-1/2', outcome: 1 },
          ],
        }),
      });
    });

    await page.goto('/chess/laplace?q=Kasparov&outcome=win&side=black');
    await expect(page.getByRole('heading', { name: 'Games Laplace played' })).toBeVisible();
    await expect(page.getByRole('link', { name: 'Kasparov' })).toBeVisible();
    await expect(page.getByText('Other', { exact: true })).toHaveCount(0);
    await expect(page.getByLabel('Outcome')).toHaveValue('win');
    await expect(page.getByLabel('Side')).toHaveValue('black');

    await page.getByLabel('Side').selectOption('all');
    expect(new URL(page.url()).searchParams.get('q')).toBe('Kasparov');
    expect(new URL(page.url()).searchParams.get('outcome')).toBe('win');
    expect(new URL(page.url()).searchParams.has('side')).toBe(false);
  });

  test('play tab loads and shows board', async ({ page }) => {
    await page.goto('/');
    const sessionPromise = page.waitForResponse((response) =>
      response.url().endsWith('/chess/play/start') && response.request().method() === 'POST');
    await page.getByRole('button', { name: 'Play', exact: true }).click();
    const sessionResponse = await sessionPromise;
    await expectOk(sessionResponse);
    await expect(page.getByRole('button', { name: 'New game' })).toBeVisible();
    await expect(page.getByRole('grid')).toBeVisible();
  });

  // The gauntlet's whole premise is that you can read the command before you spend an hour
  // of engine time on it — and that the command is the one the server would actually run,
  // built by CutechessRunner.BuildArguments rather than re-derived in the browser.
  test('gauntlet previews the exact cutechess command', async ({ page }) => {
    await page.goto('/lab/gauntlet');
    await expect(page.getByRole('heading', { name: 'Engine Gauntlet' })).toBeVisible();

    const command = page.locator('code').first();
    await expect(command).toContainText('cutechess-cli');
    await expect(command).toContainText('proto=uci');
    // A bare -debug is rejected by cutechess-cli and kills the match before game one.
    await expect(command).toContainText('-debug all');

    await page.getByLabel('Games', { exact: true }).fill('7');
    await expect(command).toContainText('-rounds 7');
  });

  test('lab surfaces are separate routes, each with its own jobs', async ({ page }) => {
    await page.goto('/lab');
    await expect(page.getByRole('heading', { name: 'Chess Lab' })).toBeVisible();
    // cutechess is no longer one card among the substrate experiments.
    await expect(page.getByRole('option', { name: /cutechess/ })).toHaveCount(0);

    await page.getByRole('button', { name: 'Gauntlet', exact: true }).click();
    await expect(page).toHaveURL(/\/lab\/gauntlet$/);
    await expect(page.getByRole('heading', { name: 'Engine Gauntlet' })).toBeVisible();

    await page.getByRole('button', { name: 'Lichess', exact: true }).click();
    await expect(page).toHaveURL(/\/lab\/lichess$/);
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
    await expectOk(response);

    await expect(page.getByText('Completed', { exact: true }).first()).toBeVisible({ timeout: 15_000 });
  });
});
