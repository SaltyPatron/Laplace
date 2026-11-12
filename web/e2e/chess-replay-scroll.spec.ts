import { test, expect } from '@playwright/test';

const GAME_ID = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';
const START_FEN = 'rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1';

test.describe('chess UI', () => {
  test('replay stepping never steals page scroll', async ({ page }) => {
    // One read gives the headers (as PGN tags) and the replayed plies with their clocks.
    await page.route(`**/modular/api/chess/game?id=${GAME_ID}`, async (route) => {
      await route.fulfill({
        contentType: 'application/json',
        body: JSON.stringify({
          id: GAME_ID,
          line: null,
          tags: [
            { name: 'Event', value: 'Scroll ownership regression' },
            { name: 'Termination', value: 'Normal' },
            { name: 'TimeControl', value: '600+0' },
          ],
          white: 'White player',
          black: 'Black player',
          result: '1-0',
          date: '2026.09.02',
          event: 'Scroll ownership regression',
          eco: 'C20',
          start_fen: START_FEN,
          movetext: Array.from({ length: 200 }, (_, i) => `${i + 1}. e4 e5`).join(' '),
          complete: true,
          plies: [
            {
              ply: 1,
              san: 'e4',
              uci: 'e2e4',
              fen: 'rnbqkbnr/pppppppp/8/8/4P3/8/PPPP1PPP/RNBQKBNR b KQkq - 0 1',
              white_moved: true,
              position: '11111111111111111111111111111111',
              clock: '0:09:58.5',
            },
            {
              ply: 2,
              san: 'e5',
              uci: 'e7e5',
              fen: 'rnbqkbnr/pppp1ppp/8/4p3/4P3/8/PPPP1PPP/RNBQKBNR w KQkq - 0 2',
              white_moved: false,
              position: '22222222222222222222222222222222',
              clock: '9:57',
            },
          ],
        }),
      });
    });

    await page.goto(`/chess/games/${GAME_ID}`);
    await expect(page.getByText('Starting position')).toBeVisible();
    // Termination and TimeControl are read from the game's tags; players link by name.
    await expect(page.getByText('600+0', { exact: true })).toBeVisible();
    await expect(page.getByRole('link', { name: 'White player', exact: true })).toHaveAttribute('href', '/chess/players/White%20player');
    // Clock strings ("H:MM:SS.f" and "M:SS") are shown as minutes and seconds.
    await expect(page.getByRole('button', { name: /^e4/ })).toContainText('9:59');
    await expect(page.getByRole('button', { name: /^e5/ })).toContainText('9:57');

    await expect(page.locator('[data-chess-scroll-root]')).toBeVisible();
    // The application shell scrolls the document; that is the scroll position the replay must not reclaim.
    const scrollTop = () => page.evaluate(() => document.scrollingElement!.scrollTop);
    const maxScroll = await page.evaluate(() => document.scrollingElement!.scrollHeight - document.scrollingElement!.clientHeight);
    expect(maxScroll).toBeGreaterThan(100);

    await page.evaluate(() => { document.scrollingElement!.scrollTop = document.scrollingElement!.scrollHeight; });
    await expect.poll(scrollTop).toBeGreaterThan(100);
    const before = await scrollTop();

    // GameBoard owns ArrowRight even while its replay is off-screen. Advancing a
    // ply may scroll the move-list viewport, but must not reclaim the application
    // scroll container from a user who deliberately moved elsewhere on the page.
    await page.keyboard.press('ArrowRight');
    await expect(page.getByText('White played e4')).toBeAttached();
    await page.waitForTimeout(50);

    expect(await scrollTop()).toBe(before);
  });
});
