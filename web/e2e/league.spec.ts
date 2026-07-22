import { test, expect } from '@playwright/test';

// The unified shell: every surface is a route, and the header nav is identical
// everywhere — no page hides another. Previously entering Explore dropped the
// nav to two items and stranded you.
test('full nav is present on every surface, including deep inside Explore', async ({ page }) => {
  await page.goto('/explore/matchup/dog/cat');
  for (const tab of ['Home', 'Chat', 'Query', 'Explore', 'Play', 'Lab', 'Billing']) {
    await expect(page.getByRole('button', { name: tab, exact: true })).toBeVisible();
  }
  // and the nav actually leaves Explore
  await page.getByRole('button', { name: 'Query', exact: true }).click();
  await expect(page).toHaveURL(/\/query$/);
});

test('every tab deep-links by URL (refresh-safe, back-button)', async ({ page }) => {
  await page.goto('/query');
  await expect(page.getByRole('button', { name: 'Run query' })).toBeVisible({ timeout: 15_000 });
  await page.goto('/billing');
  await expect(page.getByRole('heading', { name: 'Plans' })).toBeVisible({ timeout: 15_000 });
});

// Home is the league front page: live scoreboard + per-arena leaderboards.
test('Home shows the live scoreboard and league leaders', async ({ page }) => {
  await page.goto('/');
  await expect(page.getByLabel('Live substrate scoreboard')).toBeVisible();
  // the status pill reads one of the real states
  await expect(page.getByText(/folding|idle|unreachable/i).first()).toBeVisible({ timeout: 10_000 });
  await expect(page.getByText('League leaders')).toBeVisible();
});

// Head-to-head: the tale of the tape from contrast() renders both cards.
test('matchup renders records and the tale of the tape', async ({ page }) => {
  await page.goto('/explore/matchup/dog/cat');
  await expect(page.getByText('Tale of the tape')).toBeVisible({ timeout: 20_000 });
  await expect(page.getByText('shared', { exact: false })).toBeVisible();
  // both competitor names link to their entity pages
  await expect(page.getByRole('link', { name: 'dog' })).toBeVisible();
  await expect(page.getByRole('link', { name: 'cat' })).toBeVisible();
});

// Player card: the entity header leads with the rated-competitor stat row.
test('entity page leads with the player-card stat row', async ({ page }) => {
  await page.goto('/explore/resolve/whale');
  await expect(page.getByText('top rating')).toBeVisible({ timeout: 15_000 });
  await expect(page.getByText('games', { exact: true })).toBeVisible();
});
