import { test, expect } from '@playwright/test';

const GAME_ID = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';
const START_FEN = 'rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1';

test.describe('chess UI', () => {
  test('replay stepping never steals page scroll', async ({ page }) => {
    await page.route(`**/v1/chess/games/${GAME_ID}**`, async (route) => {
      const url = new URL(route.request().url());
      if (url.pathname.endsWith('/plies')) {
        await route.fulfill({
          contentType: 'application/json',
          body: JSON.stringify({
            object: 'chess.game.plies',
            game_id: GAME_ID,
            start_fen: START_FEN,
            has_clocks: false,
            truncated: null,
            plies: [
              {
                ply: 1,
                san: 'e4',
                uci: 'e2e4',
                fen: 'rnbqkbnr/pppppppp/8/8/4P3/8/PPPP1PPP/RNBQKBNR b KQkq - 0 1',
                white_moved: true,
                clock_seconds: null,
                position_id: '11111111111111111111111111111111',
              },
              {
                ply: 2,
                san: 'e5',
                uci: 'e7e5',
                fen: 'rnbqkbnr/pppp1ppp/8/4p3/4P3/8/PPPP1PPP/RNBQKBNR w KQkq - 0 2',
                white_moved: false,
                clock_seconds: null,
                position_id: '22222222222222222222222222222222',
              },
            ],
          }),
        });
        return;
      }

      await route.fulfill({
        contentType: 'application/json',
        body: JSON.stringify({
          object: 'chess.game',
          id: GAME_ID,
          white_id: '33333333333333333333333333333333',
          white: 'White player',
          black_id: '44444444444444444444444444444444',
          black: 'Black player',
          result: '1-0',
          played_on: '2026.09.02',
          event: 'Scroll ownership regression',
          eco: 'C20',
          termination: 'Normal',
          time_control: '600+0',
          tc_class: 'rapid',
          movetext: Array.from({ length: 200 }, (_, i) => `${i + 1}. e4 e5`).join(' '),
        }),
      });
    });

    await page.goto(`/chess/games/${GAME_ID}`);
    await expect(page.getByText('Starting position')).toBeVisible();

    const maxScroll = await page.evaluate(
      () => document.documentElement.scrollHeight - window.innerHeight,
    );
    expect(maxScroll).toBeGreaterThan(100);

    await page.evaluate(() => window.scrollTo(0, document.documentElement.scrollHeight));
    await expect.poll(() => page.evaluate(() => window.scrollY)).toBeGreaterThan(100);
    const before = await page.evaluate(() => window.scrollY);

    // GameBoard owns ArrowRight even while its replay is off-screen. Advancing a
    // ply may scroll the move-list viewport, but must not reclaim the document
    // scroll position from a user who deliberately moved elsewhere on the page.
    await page.keyboard.press('ArrowRight');
    await expect(page.getByText('White played e4')).toBeAttached();
    await page.waitForTimeout(50);

    const after = await page.evaluate(() => window.scrollY);
    expect(after).toBe(before);
  });
});
