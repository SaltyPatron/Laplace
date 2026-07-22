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

// The semantic-mesh drill-down: the tiered master/detail over the factorization
// of meaning — belongs_to (up) | node | roster (down), re-centering on click.
test('mesh landing explains the ladder and enters at a node', async ({ page }) => {
  await page.goto('/explore/mesh');
  await expect(page.getByRole('heading', { name: 'The mesh' })).toBeVisible();
  await expect(page.getByText('ILI concept').first()).toBeVisible();
  await page.getByRole('button', { name: 'whale', exact: true }).click();
  await expect(page).toHaveURL(/\/explore\/mesh\/[0-9a-f]{32}/i, { timeout: 15_000 });
});

test('mesh drill shows belongs-to, roster, and re-centers on a member', async ({ page }) => {
  await page.goto('/explore/mesh/014488e93e050f3f0f19ed9847ec5d65');
  await expect(page.getByText('Belongs to')).toBeVisible({ timeout: 15_000 });
  await expect(page.getByText('Roster')).toBeVisible();
  // a roster member re-centers the drill (URL changes to that node)
  const before = page.url();
  await page.locator('button', { hasText: 'whale' }).first().click();
  await expect(page).not.toHaveURL(before, { timeout: 15_000 });
  await expect(page.getByText('Belongs to')).toBeVisible();
});

// The omni-modal map is honest: the resident modality reads live, the absent
// ones read "awaiting ingest" — never a faked scoreboard.
test('mesh landing shows the honest modality map', async ({ page }) => {
  await page.goto('/explore/mesh');
  await expect(page.getByText('One law, every modality')).toBeVisible({ timeout: 15_000 });
  // Text is the resident foundation → live
  const text = page.locator('section[aria-label="Modalities"]').getByText('Text', { exact: true });
  await expect(text).toBeVisible();
  // an unseeded modality is honestly labelled, not shown as a zero scoreboard
  await expect(page.getByText('awaiting ingest').first()).toBeVisible();
});

// The provenance tier: warehouse → stage → source → roster → entity. The
// stage→source drill was broken (cli names never matched live keys) and the
// source page had no roster at all.
test('stage drills into a live source with a roster', async ({ page }) => {
  await page.goto('/explore/stage/knowledge');
  await expect(page.getByText('Stage — knowledge')).toBeVisible({ timeout: 15_000 });
  await page.getByText('wordnet', { exact: true }).click();
  await expect(page).toHaveURL(/\/explore\/source\//, { timeout: 15_000 });
  await expect(page.getByText('Roster — what this witness asserts')).toBeVisible();
  // the roster samples real testimony rows with entity links
  await expect(page.locator('a[href*="/explore/entity/"]').first()).toBeVisible({ timeout: 30_000 });
});

test('unseeded cadence source is honestly labelled on the stage page', async ({ page }) => {
  await page.goto('/explore/stage/knowledge');
  await expect(page.getByText('not yet ingested').first()).toBeVisible({ timeout: 15_000 });
});
