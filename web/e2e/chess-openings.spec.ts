import { expect, test } from '@playwright/test';

// Openings are read from the modular substrate: the UI sends a FEN or movetext and renders
// what comes back (never computing position ids). These mock the agreed contract.
const START_FEN = 'rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1';
const GAME_ID = 'cccccccccccccccccccccccccccccccc';
const OPEN_GAME = { line: 'a1'.repeat(16), name: 'Open Game', eco: 'C20', plies: 2 };
const KINGS_KNIGHT = { line: 'b2'.repeat(16), name: "King's Knight Opening", eco: 'C40', plies: 3 };
const PETROV = { line: 'c3'.repeat(16), name: 'Petrov Defence', eco: 'C42', plies: 4 };

test('the board shows the openings its position belongs to, most specific first', async ({ page }) => {
  const asked: string[] = [];
  await page.route('**/modular/api/chess/position?**', (route) => {
    asked.push(new URL(route.request().url()).searchParams.get('fen') ?? '');
    return route.fulfill({ json: { position: 'd4'.repeat(16), games: 12873, openings: [OPEN_GAME, KINGS_KNIGHT] } });
  });
  await page.route(/\/chess\/(legal|eval|new)$/, (route) => {
    const path = new URL(route.request().url()).pathname;
    if (path.endsWith('/legal')) return route.fulfill({ json: { moves: [], inCheck: false, status: 'ongoing' } });
    if (path.endsWith('/eval')) return route.fulfill({ json: { whiteScoreCp: 0, depth: 1, nodes: 1 } });
    return route.fulfill({ json: { fen: START_FEN } });
  });
  await page.goto('/play');
  const panel = page.getByRole('region', { name: 'Openings of this position' });
  await expect(panel).toContainText("King's Knight Opening");
  await expect(panel).toContainText('C40');
  await expect(panel.getByRole('list', { name: 'Broader openings containing this position' })).toContainText('Open Game');
  await expect(panel).toContainText('Reached in 12,873 recorded games.');
  expect(asked.at(-1)).toBe(START_FEN);
});

test('a position on no opening line says so', async ({ page }) => {
  await page.route('**/modular/api/chess/position?**', (route) =>
    route.fulfill({ json: { position: 'e5'.repeat(16), games: 0, openings: [] } }));
  await page.route(/\/chess\/(legal|eval|new)$/, (route) => route.fulfill({ json: { moves: [], inCheck: false, status: 'ongoing', fen: START_FEN } }));
  await page.goto('/play');
  const panel = page.getByRole('region', { name: 'Openings of this position' });
  await expect(panel).toContainText('No named opening contains this position.');
  await expect(panel).toContainText('No recorded game reached it.');
});

test('a game shows its deepest opening, where it leaves book, and each ply\'s openings', async ({ page }) => {
  const plies = [
    { ply: 1, san: 'e4', uci: 'e2e4', fen: 'rnbqkbnr/pppppppp/8/8/4P3/8/PPPP1PPP/RNBQKBNR b KQkq - 0 1', white_moved: true },
    { ply: 2, san: 'e5', uci: 'e7e5', fen: 'rnbqkbnr/pppp1ppp/8/4p3/4P3/8/PPPP1PPP/RNBQKBNR w KQkq - 0 2', white_moved: false },
    { ply: 3, san: 'Nf3', uci: 'g1f3', fen: 'rnbqkbnr/pppp1ppp/8/4p3/4P3/5N2/PPPP1PPP/RNBQKB1R b KQkq - 1 2', white_moved: true },
    { ply: 4, san: 'Nf6', uci: 'g8f6', fen: 'rnbqkb1r/pppp1ppp/5n2/4p3/4P3/5N2/PPPP1PPP/RNBQKB1R w KQkq - 2 3', white_moved: false },
    { ply: 5, san: 'h3', uci: 'h2h3', fen: 'rnbqkb1r/pppp1ppp/5n2/4p3/4P3/5N1P/PPPP1PP1/RNBQKB1R b KQkq - 0 3', white_moved: true },
  ].map((row, i) => ({ ...row, clock_seconds: null, position_id: String(i + 1).repeat(32) }));
  const posted: unknown[] = [];
  await page.route(`**/v1/chess/games/${GAME_ID}**`, (route) => route.fulfill({ json: new URL(route.request().url()).pathname.endsWith('/plies')
    ? { object: 'chess.game.plies', game_id: GAME_ID, start_fen: START_FEN, has_clocks: false, truncated: null, plies }
    : { object: 'chess.game', id: GAME_ID, white_id: null, white: 'White', black_id: null, black: 'Black', result: '1-0',
        played_on: null, event: null, eco: 'C42', termination: null, time_control: null, tc_class: null,
        movetext: '1. e4 e5 2. Nf3 Nf6 3. h3 1-0' } }));
  await page.route('**/modular/api/chess/game-openings', (route) => {
    posted.push(route.request().postDataJSON());
    return route.fulfill({ json: {
      line: 'f6'.repeat(16), deepest: PETROV, out_of_book_at: 5,
      plies: [
        { ply: 0, position: '0'.repeat(32), openings: [] },
        { ply: 1, position: '1'.repeat(32), openings: [] },
        { ply: 2, position: '2'.repeat(32), openings: [OPEN_GAME] },
        { ply: 3, position: '3'.repeat(32), openings: [OPEN_GAME, KINGS_KNIGHT] },
        { ply: 4, position: '4'.repeat(32), openings: [OPEN_GAME, KINGS_KNIGHT, PETROV] },
        { ply: 5, position: '5'.repeat(32), openings: [] },
      ] } });
  });
  await page.goto(`/chess/games/${GAME_ID}`);
  const opening = page.getByRole('region', { name: 'Opening', exact: true });
  await expect(opening).toContainText('Petrov Defence');
  await expect(opening).toContainText('C42');
  await expect(opening).toContainText('Leaves book at ply 5 (3. h3).');
  expect(posted).toEqual([{ pgn: '1. e4 e5 2. Nf3 Nf6 3. h3 1-0' }]);

  const atMove = page.getByRole('region', { name: 'Openings at this move' });
  await expect(atMove).toContainText('The starting position.');
  const next = page.getByRole('button', { name: 'Next move' });
  for (let i = 0; i < 4; i += 1) await next.click();
  await expect(atMove).toContainText('Petrov Defence');
  await expect(atMove.getByRole('list', { name: 'Broader openings containing this position' })).toContainText("King's Knight Opening");
  await next.click();
  await expect(atMove).toContainText('Out of book: no named opening contains this position.');
});

test('a game list reads every game\'s deepest opening in one batch', async ({ page }) => {
  const row = (id: string, movetext: string | null) => ({ id: id.repeat(32), played_on: '2026.11.11', event: 'Laplace Play', eco: null,
    as_white: true, opponent_id: null, opponent: 'Browser player', result: '1-0', outcome: 2, movetext });
  await page.route('**/v1/chess/laplace/games?**', (route) => route.fulfill({ json: { object: 'chess.games', player_id: '', offset: 0,
    games: [row('a', '1. e4 e5 2. Nf3 Nf6 1-0'), row('b', '1. h4 a5 1-0'), row('c', null)] } }));
  const batches: unknown[] = [];
  await page.route('**/modular/api/chess/game-openings', (route) => {
    batches.push(route.request().postDataJSON());
    return route.fulfill({ json: [
      { deepest: PETROV },
      { deepest: null },
    ] });
  });
  await page.goto('/chess/laplace');
  const rows = page.getByRole('row');
  await expect(rows.filter({ hasText: 'Petrov Defence' })).toHaveCount(1);
  await expect(rows.filter({ hasText: 'no named line' })).toHaveCount(1);
  await expect(rows.filter({ hasText: 'no movetext' })).toHaveCount(1);
  // One request for the page, carrying only the games that have movetext, in order.
  expect(batches).toEqual([{ pgns: ['1. e4 e5 2. Nf3 Nf6 1-0', '1. h4 a5 1-0'] }]);
});

const KQK = '8/8/8/8/8/8/1Q6/K6k w - - 0 1';
const playOn = async (page: import('@playwright/test').Page, fen: string) => {
  await page.route('**/modular/api/chess/position?**', (route) => route.fulfill({ json: { position: 'f1'.repeat(16), games: 0, openings: [] } }));
  await page.route(/\/chess\/(legal|eval|new)$/, (route) => {
    const path = new URL(route.request().url()).pathname;
    if (path.endsWith('/legal')) return route.fulfill({ json: { moves: [], inCheck: false, status: 'ongoing' } });
    if (path.endsWith('/eval')) return route.fulfill({ json: { whiteScoreCp: 0, depth: 1, nodes: 1 } });
    return route.fulfill({ json: { fen } });
  });
};

test('a full board never asks the tablebase', async ({ page }) => {
  let asked = 0;
  await page.route('**/modular/api/chess/tablebase?**', (route) => { asked += 1; return route.fulfill({ json: {} }); });
  await playOn(page, START_FEN);
  await page.goto('/play');
  await expect(page.getByRole('region', { name: 'Openings of this position' })).toBeVisible();
  await expect(page.getByRole('region', { name: 'Endgame tablebase' })).toHaveCount(0);
  expect(asked).toBe(0);
});

test('a tablebase position shows its verdict and best moves from the mover\'s view', async ({ page }) => {
  await page.route('**/modular/api/chess/tablebase?**', (route) => route.fulfill({ json: {
    position: 'aa'.repeat(16), wdl: 'win', dtz: 11,
    moves: [
      { san: 'Kb1', position: 'b1'.repeat(16), wdl: 'loss', dtz: -10 },
      { san: 'Qb1', position: 'c1'.repeat(16), wdl: 'draw', dtz: 0 },
      { san: 'Qg2', position: 'd1'.repeat(16), wdl: 'blessed loss', dtz: -101 },
    ] } }));
  await playOn(page, KQK);
  await page.goto('/play');
  await page.getByRole('button', { name: 'New game' }).click();
  const panel = page.getByRole('region', { name: 'Endgame tablebase' });
  await expect(panel).toContainText('White to move wins with best play');
  await expect(panel).toContainText('distance to zeroing 11');
  const moves = panel.getByRole('list', { name: 'Best moves by tablebase verdict' });
  await expect(moves.getByRole('listitem').first()).toContainText('Kb1');
  await expect(moves.getByRole('listitem').first()).toContainText('wins, 10');
  await expect(moves.getByRole('listitem').nth(1)).toContainText('Qb1 draws');
  await expect(moves.getByRole('listitem').nth(2)).toContainText('Qg2 wins, but not within the 50-move rule');
});

test('a few-piece position without a recorded table says so', async ({ page }) => {
  await page.route('**/modular/api/chess/tablebase?**', (route) => route.fulfill({ json: { position: 'bb'.repeat(16), wdl: null, dtz: null, moves: [] } }));
  await playOn(page, '8/8/8/8/8/8/1QR5/K6k w - - 0 1');
  await page.goto('/play');
  await page.getByRole('button', { name: 'New game' }).click();
  await expect(page.getByRole('region', { name: 'Endgame tablebase' })).toContainText('No tablebase verdict is recorded for this 4-piece position.');
});
