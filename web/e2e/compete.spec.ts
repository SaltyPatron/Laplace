import { test, expect } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';

// Point LAPLACE_E2E_URL at Vite; the fixture draws invented competitions with the production components.
test.skip(process.env.LAPLACE_E2E_FIXTURES !== '1', 'requires the Vite-only interaction fixtures');

test('a wide bracket joins its rounds with connectors and nothing overlaps', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto('/tests/compete.html?view=single&size=16&played=10');
  await expect(page.getByRole('article', { name: 'Final', exact: true })).toBeVisible();
  await expect(page.locator('svg path')).toHaveCount(14);
  const boxes = await page.getByRole('article').evaluateAll((cards) => cards.map((card) => card.getBoundingClientRect().toJSON()));
  expect(boxes).toHaveLength(15);
  for (const a of boxes) for (const b of boxes) {
    if (a === b) continue;
    expect(a.right <= b.left || b.right <= a.left || a.bottom <= b.top || b.bottom <= a.top).toBe(true);
  }
  expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([]);
});

test('a connector meets the slot it fills', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto('/tests/compete.html?view=single&size=4&played=0');
  const final = await page.getByRole('article', { name: 'Final', exact: true }).getByRole('listitem').first().evaluate((slot) => slot.getBoundingClientRect().toJSON());
  const end = await page.locator('svg path').first().evaluate((path) => {
    const line = path as SVGPathElement;
    const point = line.getPointAtLength(line.getTotalLength()).matrixTransform(line.getScreenCTM()!);
    return { x: point.x, y: point.y };
  });
  expect(Math.abs(end.x - final.left)).toBeLessThan(2);
  expect(Math.abs(end.y - (final.top + final.height / 2))).toBeLessThan(2);
});

test('on a phone the same bracket is one round per swipe, with no sideways page scroll', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 800 });
  await page.goto('/tests/compete.html?view=single&size=16&played=10');
  await expect(page.locator('svg')).toBeHidden();
  await expect(page.getByRole('radio', { name: 'Schedule' })).toBeInViewport({ ratio: 1 });
  await expect(page.getByRole('article', { name: 'Round of 16 1' }).getByText('262')).toBeInViewport({ ratio: 1 });
  await expect(page.getByRole('region', { name: 'Round of 16' })).toBeInViewport();
  await expect(page.getByRole('region', { name: 'Final', exact: true })).not.toBeInViewport({ ratio: 0.5 });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
  expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([]);
});

test('pressing an entrant follows them through every round', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto('/tests/compete.html?view=single&size=8&played=7');
  const champion = await page.getByRole('article', { name: 'Final', exact: true }).locator('[data-won] button').getAttribute('aria-label');
  await page.getByRole('article', { name: 'Final', exact: true }).getByRole('button', { name: champion! }).click();
  await expect(page.locator('li[data-on]')).toHaveCount(3);
  await expect(page.locator('svg path[data-on]')).toHaveCount(2);
});

test('each stage is drawn in the shape its matches have', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto('/tests/compete.html?view=sweeper&played=3');
  await expect(page.getByRole('columnheader', { name: 'Place' })).toBeVisible();
  await page.getByRole('radio', { name: 'Stepladder' }).click();
  await expect(page.getByRole('article', { name: 'Title match' })).toBeVisible();
  await page.goto('/tests/compete.html?view=robin&size=6&played=9');
  await expect(page.getByRole('rowheader')).toHaveCount(6);
  await expect(page.getByRole('region', { name: 'Standings' })).toBeVisible();
});
