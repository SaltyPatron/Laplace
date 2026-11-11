import { expect, test } from '@playwright/test';

// Pages lay out against their column (window minus rail and inspector), not the window.
// 868px is a laptop window beside the collapsed rail; 1080px keeps the full 15rem rail,
// which leaves a narrower column than 1024px did when pages still asked the viewport.
const WIDTHS = [868, 1080];
const ROUTES = ['/', '/chat', '/query', '/data', '/explore', '/proof', '/unicode', '/chess', '/play', '/lab', '/billing', '/settings'];

for (const width of WIDTHS) {
  test.describe(`at ${width}px`, () => {
    test.use({ viewport: { width, height: 900 } });

    for (const route of ROUTES) {
      test(`${route} fits its column without sideways scroll`, async ({ page }) => {
        await page.goto(route);
        await page.locator('main').waitFor();
        // Let the first reads settle into whatever state they reach (data, empty or error).
        await page.waitForLoadState('networkidle', { timeout: 4000 }).catch(() => undefined);
        const overflow = await page.evaluate(() => {
          const root = document.scrollingElement ?? document.documentElement;
          const main = document.querySelector('main')!;
          const column = main.getBoundingClientRect();
          const escapes = [...main.querySelectorAll<HTMLElement>('*')]
            .filter((el) => {
              const box = el.getBoundingClientRect();
              if (!box.width || !el.checkVisibility?.()) return false;
              // Content inside its own scroll box (tables, code) is allowed to be wider.
              for (let up = el.parentElement; up && up !== main; up = up.parentElement) {
                const { overflowX } = getComputedStyle(up);
                if (overflowX !== 'visible' && overflowX !== 'clip') return false;
              }
              return box.right > column.right + 1 || box.left < column.left - 1;
            })
            .slice(0, 5)
            .map((el) => `${el.tagName.toLowerCase()}.${String(el.className).split(' ')[0]} ${Math.round(el.getBoundingClientRect().right)}px`);
          return { page: root.scrollWidth - root.clientWidth, escapes };
        });
        expect(overflow.page, 'the page scrolls sideways').toBeLessThanOrEqual(0);
        expect(overflow.escapes, `elements wider than the ${width}px column`).toEqual([]);
      });
    }
  });
}
