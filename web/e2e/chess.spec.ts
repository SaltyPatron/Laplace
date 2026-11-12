import { test, expect } from '@playwright/test';
import { stubApi } from './fixtures/api';

async function expectOk(response: import('@playwright/test').Response) {
  const failureBody = response.ok() ? undefined : await response.text();
  expect(response.ok(), failureBody).toBe(true);
}

test.describe('chess UI', () => {
  test.beforeEach(async ({ page }) => { await stubApi(page); });

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
        body = { engines: Object.fromEntries(['fastchess', 'cutechess', 'stockfish', 'qt', 'laplaceUci']
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
    await page.getByRole('spinbutton', { name: 'Stockfish threads', exact: true }).fill('4');
    await page.getByRole('spinbutton', { name: 'Stockfish hash (MiB)', exact: true }).fill('256');
    await page.getByRole('textbox', { name: 'Stockfish NUMA policy', exact: true }).fill('system');
    await page.getByRole('textbox', { name: 'Stockfish tablebase paths', exact: true }).fill('/tables/3-5:/tables/6');
    await expect.poll(() => previews.at(-1)?.searchParams.get('stockfishThreads')).toBe('4');
    await expect.poll(() => previews.at(-1)?.searchParams.get('stockfishHashMb')).toBe('256');
    await expect.poll(() => previews.at(-1)?.searchParams.get('stockfishNumaPolicy')).toBe('system');
    await expect.poll(() => previews.at(-1)?.searchParams.get('stockfishSyzygyPath')).toBe('/tables/3-5:/tables/6');
    await startButton.click();
    await expect.poll(() => starts.length).toBe(2);
    expect(starts[1].config).toMatchObject({ elo: '2300', limitStrength: 'false',
      stockfishThreads: '4', stockfishHashMb: '256', stockfishNumaPolicy: 'system',
      stockfishSyzygyPath: '/tables/3-5:/tables/6' });
    expect(startOperatorHeaders[1]).toBeUndefined();

    await limitStrength.click();
    await expect(limitStrength).toBeChecked();
    await expect(elo).toBeEnabled();
    await expect(elo).toHaveValue('2300');
  });

  test('player search and paging are URL-addressable', async ({ page }) => {
    const requests: URL[] = [];
    await page.route('**/modular/api/chess/players?**', async (route) => {
      const url = new URL(route.request().url());
      requests.push(url);
      await route.fulfill({ json: {
        search: url.searchParams.get('search'),
        total: 101,
        offset: Number(url.searchParams.get('offset') ?? 0),
        players: [
          { name: 'Karpov, Anatoly', games: 2056, games_complete: false },
          { name: 'Karpov, Alexander', games: 12, games_complete: true },
        ],
      } });
    });

    await page.goto('/chess?q=Karpov&offset=50');
    const player = page.getByRole('link', { name: 'Karpov, Anatoly' });
    await expect(player).toBeVisible();
    // A player is addressed by the name as written, not by an id.
    await expect(player).toHaveAttribute('href', '/chess/players/Karpov%2C%20Anatoly');
    await expect(page.getByRole('textbox', { name: 'Find a chess player' })).toHaveValue('Karpov');
    expect(requests.at(-1)?.searchParams.get('search')).toBe('Karpov');
    expect(requests.at(-1)?.searchParams.get('offset')).toBe('50');
    expect(requests.at(-1)?.searchParams.get('limit')).toBe('50');
    // A count that hit the read's cap is shown as "N+"; a complete one is exact.
    await expect(page.getByRole('row').filter({ hasText: 'Karpov, Anatoly' })).toContainText('2,056+');
    await expect(page.getByRole('row').filter({ hasText: 'Karpov, Alexander' }).getByRole('cell').last()).toHaveText('12');
    // Names are the whole identity: no Laplace rating or standing column exists.
    await expect(page.getByRole('columnheader')).toHaveText(['Player', 'Games']);

    await page.getByRole('button', { name: 'Next ›', exact: true }).click();
    await expect(page).toHaveURL(/q=Karpov/);
    await expect(page).toHaveURL(/offset=100/);
    await expect.poll(() => requests.at(-1)?.searchParams.get('offset')).toBe('100');
    await expect(page.getByRole('button', { name: 'Next ›', exact: true })).toBeDisabled();

    // A new search starts from its first page.
    await page.getByRole('textbox', { name: 'Find a chess player' }).fill('Tal');
    await page.getByRole('button', { name: 'Search players', exact: true }).click();
    await expect(page).toHaveURL(/q=Tal/);
    expect(new URL(page.url()).searchParams.has('offset')).toBe(false);
  });

  test('the player index asks for a search instead of reading a roster', async ({ page }) => {
    let reads = 0;
    await page.route('**/modular/api/chess/players?**', (route) => { reads += 1; return route.fulfill({ json: { search: '', total: 0, offset: 0, players: [] } }); });
    await page.goto('/chess');
    await expect(page.getByText('Search for a player to see their recorded games.', { exact: true })).toBeVisible();
    await expect(page.getByRole('link', { name: 'Games Laplace played' })).toHaveAttribute('href', '/chess/laplace');
    await expect(page.getByRole('table')).toHaveCount(0);
    expect(reads).toBe(0);
  });

  test('a player page reads one name: record by colour, source ratings, opponents and paged games', async ({ page }) => {
    const requests: URL[] = [];
    const record = (games: number, wins: number, draws: number, losses: number) =>
      ({ games, wins, draws, losses, unscored: 0, score: (wins + draws / 2) / games });
    await page.route('**/modular/api/chess/player?**', (route) => {
      const url = new URL(route.request().url());
      requests.push(url);
      const offset = Number(url.searchParams.get('offset') ?? 0);
      return route.fulfill({ json: {
        name: 'Karpov, Anatoly',
        overall: record(60, 30, 10, 20), as_white: record(30, 18, 6, 6), as_black: record(30, 12, 4, 14),
        peak_rating: 2780, ratings: [{ rating: 2780, games: 4 }, { rating: 2725, games: 9 }],
        opponents_total: 40,
        opponents: [{ name: 'Kasparov, Garry', ...record(20, 5, 10, 5) }],
        total: 60, offset,
        games: [{ id: (offset === 0 ? 'a' : 'b').repeat(32), line: null, white: 'Karpov, Anatoly', black: 'Kasparov, Garry',
          result: '1-0', date: offset === 0 ? '1990.10.08' : '1985.09.03', event: 'World Championship', eco: 'C92', movetext: null,
          as_white: true, opponent: 'Kasparov, Garry', outcome: 2 }],
      } });
    });
    await page.route('**/modular/api/chess/game?**', (route) => route.fulfill({ json: {
      id: 'a'.repeat(32), line: null, tags: [], white: 'Karpov, Anatoly', black: 'Kasparov, Garry', result: '1-0',
      date: '1990.10.08', event: 'World Championship', eco: 'C92', start_fen: null, movetext: null, complete: true, plies: [] } }));

    await page.goto('/chess/players/Karpov%2C%20Anatoly');
    await expect(page.getByRole('heading', { name: 'Karpov, Anatoly', exact: true })).toBeVisible();
    expect(requests[0].searchParams.get('name')).toBe('Karpov, Anatoly');
    await expect(page.getByRole('row').filter({ hasText: 'As White' })).toContainText('18–6–6');
    await expect(page.getByText('highest Elo a game tagged', { exact: true })).toBeVisible();
    // Opponents carry the page player's wins, draws and losses, and link by name.
    const opponent = page.getByRole('region', { name: 'Head to head' }).getByRole('row').filter({ hasText: 'Kasparov, Garry' });
    await expect(opponent).toContainText('5–5–10');
    await expect(opponent.getByRole('link', { name: 'Kasparov, Garry' })).toHaveAttribute('href', '/chess/players/Kasparov%2C%20Garry');
    await expect(page.getByText('The 1 most played of 40 opponents.', { exact: false })).toBeVisible();

    // Games page by the read's own total.
    await expect(page.getByText('1–1 of 60', { exact: true })).toBeVisible();
    await page.getByRole('button', { name: 'Older ›', exact: true }).click();
    await expect(page).toHaveURL(/offset=25/);
    await expect.poll(() => requests.at(-1)?.searchParams.get('offset')).toBe('25');
    await expect(page.getByRole('link', { name: '1985.09.03', exact: true })).toHaveAttribute('href', `/chess/games/${'b'.repeat(32)}`);
  });

  test('a name nobody played under says so instead of showing an empty record', async ({ page }) => {
    const empty = { games: 0, wins: 0, draws: 0, losses: 0, unscored: 0, score: null };
    await page.route('**/modular/api/chess/player?**', (route) => route.fulfill({ json: {
      name: 'Profile, Only', overall: empty, as_white: empty, as_black: empty, peak_rating: null, ratings: [],
      opponents_total: 0, opponents: [], total: 0, offset: 0, games: [] } }));
    await page.goto('/chess/players/Profile%2C%20Only');
    await expect(page.getByRole('heading', { name: 'Profile, Only', exact: true })).toBeVisible();
    await expect(page.getByText('No recorded games under this name.', { exact: true })).toBeVisible();
    await expect(page.getByRole('table')).toHaveCount(0);
    await expect(page.getByText('Peak rating', { exact: true })).toHaveCount(0);
  });

  test('Laplace games have a dedicated, filterable URL', async ({ page }) => {
    const requests: URL[] = [];
    await page.route('**/modular/api/chess/games?**', async (route) => {
      requests.push(new URL(route.request().url()));
      await route.fulfill({ json: {
        witness: 'Laplace',
        total: 2,
        offset: 0,
        games: [
          { id: 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', line: null, white: 'Kasparov', black: 'Laplace', result: '0-1', date: '2026.08.19', event: 'Browser game', eco: null, movetext: null },
          { id: 'cccccccccccccccccccccccccccccccc', line: null, white: 'Laplace', black: 'Other', result: '1/2-1/2', date: '2026.08.18', event: 'Lichess', eco: 'B12', movetext: null },
        ],
      } });
    });

    await page.goto('/chess/laplace?q=Kasparov&result=0-1');
    await expect(page.getByRole('heading', { name: 'Games Laplace played' })).toBeVisible();
    expect(requests[0].searchParams.get('witness')).toBe('Laplace');
    // Both names of a game are shown, each linked to that name's page.
    const game = page.getByRole('row').filter({ hasText: '2026.08.19' });
    await expect(game.getByRole('link', { name: 'Kasparov', exact: true })).toHaveAttribute('href', '/chess/players/Kasparov');
    await expect(game.getByRole('link', { name: 'Laplace', exact: true })).toHaveAttribute('href', '/chess/players/Laplace');
    await expect(page.getByText('Other', { exact: true })).toHaveCount(0);
    await expect(page.getByLabel('Result')).toHaveValue('0-1');

    await page.getByLabel('Result').selectOption('all');
    expect(new URL(page.url()).searchParams.get('q')).toBe('Kasparov');
    expect(new URL(page.url()).searchParams.has('result')).toBe(false);
  });

  test('play is reachable from the Chess rail and a new game opens a play session', async ({ page }) => {
    const START_FEN = 'rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1';
    const starts: unknown[] = [];
    await page.route(/\/chess\/(legal|eval|new|play\/start)$/, async (route) => {
      const path = new URL(route.request().url()).pathname;
      if (path.endsWith('/legal')) return route.fulfill({ json: { moves: [], inCheck: false, status: 'ongoing' } });
      if (path.endsWith('/eval')) return route.fulfill({ json: { whiteScoreCp: 20, depth: 4, nodes: 100 } });
      if (path.endsWith('/new')) return route.fulfill({ json: { fen: START_FEN } });
      starts.push(route.request().postDataJSON());
      return route.fulfill({ json: { sessionId: 'ui-only-session', fen: START_FEN, status: 'ongoing', ply: 0 } });
    });

    await page.goto('/chess');
    await page.getByRole('link', { name: 'Play', exact: true }).click();
    await expect(page).toHaveURL(/\/play$/);
    await expect(page.getByRole('grid')).toBeVisible();

    // A play session starts with the game, not on page load.
    const sessionPromise = page.waitForResponse((response) =>
      response.url().endsWith('/chess/play/start') && response.request().method() === 'POST');
    await page.getByRole('button', { name: 'New game' }).click();
    await expectOk(await sessionPromise);
    expect(starts).toHaveLength(1);
    await expect(page.getByRole('grid')).toBeVisible();
  });

  test('the board is playable from the keyboard: one tab stop, arrows, Enter selects and moves', async ({ page }) => {
    const START_FEN = 'rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1';
    const AFTER_E4 = 'rnbqkbnr/pppppppp/8/8/4P3/8/PPPP1PPP/RNBQKBNR b KQkq - 0 1';
    const moves: string[] = [];
    await page.route(/\/chess\/(legal|eval|new|play\/start|play\/move|play\/bestmove)$/, async (route) => {
      const path = new URL(route.request().url()).pathname;
      const body = route.request().postDataJSON() ?? {};
      if (path.endsWith('/legal')) return route.fulfill({ json: body.fen === START_FEN
        ? { moves: [{ uci: 'e2e4', effMu: 1500, rated: false }, { uci: 'e2e3', effMu: 1500, rated: false }], inCheck: false, status: 'ongoing' }
        : { moves: [], inCheck: false, status: 'ongoing' } });
      if (path.endsWith('/eval')) return route.fulfill({ json: { whiteScoreCp: 20, depth: 4, nodes: 100 } });
      if (path.endsWith('/new')) return route.fulfill({ json: { fen: START_FEN } });
      if (path.endsWith('/move')) { moves.push(body.uci); return route.fulfill({ json: { fen: AFTER_E4, terminal: false, status: 'ongoing', legal: true, ply: 1 } }); }
      if (path.endsWith('/bestmove')) return route.fulfill({ json: { uci: null, fen: AFTER_E4, effMu: 1500, rated: false, terminal: false, status: 'ongoing', scoreCp: 0, depth: 0, nodes: 0 } });
      return route.fulfill({ json: { sessionId: 'keyboard-session', fen: START_FEN, status: 'ongoing', ply: 0 } });
    });
    await page.goto('/play');
    await page.getByRole('button', { name: 'New game' }).click();
    const e2 = page.getByRole('gridcell', { name: /^e2: White pawn/ });
    await expect(e2).toHaveAttribute('tabindex', '0');
    await expect(page.locator('[role=gridcell][tabindex="0"]')).toHaveCount(1);
    await e2.focus();
    await page.keyboard.press('Enter');
    await expect(e2).toHaveAttribute('aria-selected', 'true');
    // Up twice from e2 reaches e4 on a board drawn with White at the bottom.
    await page.keyboard.press('ArrowUp');
    await page.keyboard.press('ArrowUp');
    await expect(page.getByRole('gridcell', { name: /^e4: empty/ })).toBeFocused();
    await page.keyboard.press('Enter');
    await expect.poll(() => moves).toEqual(['e2e4']);
    await expect(page.getByRole('gridcell', { name: /^e4: White pawn/ })).toBeVisible();
  });

  // The preview is the server's command line (CutechessRunner.BuildArguments); the page
  // sends the chosen setup and shows exactly what comes back.
  test('gauntlet previews the server command for the chosen setup', async ({ page }) => {
    const previews: URL[] = [];
    await page.route((url) => url.pathname.startsWith('/chess/lab/'), async (route) => {
      const url = new URL(route.request().url());
      if (url.pathname.endsWith('/catalog')) {
        return route.fulfill({ json: { jobs: [], engines: Object.fromEntries(['fastchess', 'cutechess', 'stockfish', 'qt', 'laplaceUci']
          .map((key) => [key, { path: `/test/${key}`, found: true, source: 'config' }])) } });
      }
      if (url.pathname.endsWith('/preview')) {
        previews.push(url);
        const rounds = url.searchParams.get('rounds');
        const conductor = url.searchParams.get('conductor');
        return route.fulfill({ json: { commandLine: `${conductor}-cli -engine proto=uci -rounds ${rounds} -debug all`, ready: true, games: Number(rounds), missing: [] } });
      }
      return route.fulfill({ json: [] });
    });

    await page.goto('/lab/gauntlet');
    await expect(page.getByRole('heading', { name: 'Engine Gauntlet' })).toBeVisible({ timeout: 15_000 });
    await page.getByRole('radio', { name: 'cutechess' }).click();
    await expect(page.getByRole('radio', { name: 'cutechess' })).toBeChecked();

    const command = page.locator('code').first();
    await expect(command).toContainText('cutechess-cli');
    await expect(command).toContainText('proto=uci');
    // A bare -debug is rejected by cutechess-cli and kills the match before game one.
    await expect(command).toContainText('-debug all');

    await page.getByLabel('Games', { exact: true }).fill('7');
    await expect.poll(() => previews.at(-1)?.searchParams.get('rounds')).toBe('7');
    await expect(command).toContainText('-rounds 7');
  });

  test('lab surfaces are separate routes, each with its own jobs', async ({ page }) => {
    await page.goto('/lab');
    await expect(page.getByRole('heading', { name: 'Chess Lab' })).toBeVisible({ timeout: 15_000 });
    // cutechess is no longer one card among the substrate experiments.
    await expect(page.getByRole('option', { name: /cutechess/ })).toHaveCount(0);

    await page.getByRole('button', { name: 'Gauntlet', exact: true }).click();
    await expect(page).toHaveURL(/\/lab\/gauntlet$/);
    await expect(page.getByRole('heading', { name: 'Engine Gauntlet' })).toBeVisible({ timeout: 15_000 });

    await page.getByRole('button', { name: 'Lichess', exact: true }).click();
    await expect(page).toHaveURL(/\/lab\/lichess$/);
  });

  test('lab tab completes a read-only job', async ({ page }) => {
    let started = false;
    await page.route((url) => url.pathname.startsWith('/chess/lab/'), async (route) => {
      const url = new URL(route.request().url());
      if (url.pathname.endsWith('/catalog')) return route.fulfill({ json: { jobs: [], engines: {} } });
      if (url.pathname.endsWith('/jobs')) {
        return route.fulfill({ json: started
          ? [{ id: 'ui-only-job', kind: 'ladder', state: 'Completed', summary: { done: 1, total: 1 }, artifacts: {} }]
          : [] });
      }
      if (url.pathname.endsWith('/start')) {
        started = true;
        return route.fulfill({ json: { jobId: 'ui-only-job' } });
      }
      if (url.pathname.endsWith('/events')) {
        return route.fulfill({ contentType: 'text/event-stream', body: 'data: {"finalState":"Completed"}\n\n' });
      }
      return route.fulfill({ status: 404, json: {} });
    });

    await page.goto('/chess');
    await page.getByRole('link', { name: 'Lab', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'Chess Lab' })).toBeVisible({ timeout: 15_000 });
    await page.getByRole('option', { name: /Eval overlay ladder/ }).click();

    const responsePromise = page.waitForResponse((response) =>
      response.url().endsWith('/chess/lab/start') && response.request().method() === 'POST');
    await page.getByRole('button', { name: 'Start experiment', exact: true }).click();
    const response = await responsePromise;
    await expectOk(response);

    await expect(page.getByText('Completed', { exact: true }).first()).toBeVisible({ timeout: 15_000 });
  });
});
