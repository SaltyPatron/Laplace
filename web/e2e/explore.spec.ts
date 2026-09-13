import { test, expect } from '@playwright/test';

const WHALE_ID = '11111111111111111111111111111111';

function browseResponse(query: string, hits: unknown[]) {
  return {
    object: 'laplace.explore.browse',
    query,
    hits,
    receipt: {
      query_root_id_hex: WHALE_ID,
      query_member_ids_hex: [WHALE_ID],
      candidate_names: hits.length,
      candidate_capacity: 2048,
      candidate_truncated: false,
      matched_entities: hits.length,
      returned: hits.length,
      offset: 0,
      limit: 50,
      elapsed_us: 731,
    },
  };
}

test('Explore tab opens the substrate browser and keeps Warehouse addressable', async ({ page }) => {
  await page.goto('/explore');
  await expect(page.getByRole('heading', { name: 'Browse Laplace like a reference site' })).toBeVisible();
  await expect(page.getByRole('textbox', { name: 'Find a starting point in the substrate' })).toBeVisible();
  const exploreNav = page.locator('aside');
  await expect(exploreNav.getByRole('link', { name: 'Browse', exact: true })).toBeVisible();
  await expect(exploreNav.getByRole('link', { name: 'Warehouse', exact: true })).toBeVisible();

  await exploreNav.getByRole('link', { name: 'Warehouse', exact: true }).click();
  await expect(page).toHaveURL('/explore/warehouse');
  await expect(page.getByRole('heading', { name: 'Substrate warehouse' })).toBeVisible();
});

test('Browse returns a canonical result set instead of silently choosing one entity', async ({ page }) => {
  await page.route('**/v1/explore/browse?**', async (route) => {
    await route.fulfill({
      contentType: 'application/json',
      body: JSON.stringify(browseResponse('Hikaru', [
        {
          id_hex: WHALE_ID,
          label: 'Nakamura, Hikaru',
          tier: 2,
          type: 'Chess_Player',
          matched_name_id_hex: '22222222222222222222222222222222',
          match_kind: 'name',
          rating: 1500,
          rd: 120,
          eff_mu: 1260,
          witnesses: 42,
        },
        {
          id_hex: '33333333333333333333333333333333',
          label: 'Hikaru',
          tier: 2,
          type: 'Word',
          matched_name_id_hex: '33333333333333333333333333333333',
          match_kind: 'surface',
          rating: null,
          rd: null,
          eff_mu: null,
          witnesses: 0,
        },
      ])),
    });
  });

  await page.goto('/explore');
  await page.getByRole('textbox', { name: 'Find a starting point in the substrate' }).fill('Hikaru');
  await page.getByRole('button', { name: 'Browse' }).click();

  await expect(page.getByRole('link', { name: 'Nakamura, Hikaru' })).toBeVisible();
  await expect(page.getByRole('link', { name: 'Hikaru', exact: true })).toBeVisible();
  await expect(page.getByText('2 canonical results')).toBeVisible();
  await expect(page.getByText('731 μs')).toBeVisible();

  await page.getByRole('link', { name: 'Nakamura, Hikaru' }).click();
  await expect(page).toHaveURL(`/explore/entity/${WHALE_ID}`);
});

test('Browse exposes capacity truncation as an execution bound the user can expand', async ({ page }) => {
  let seenCapacity = '';
  await page.route('**/v1/explore/browse?**', async (route) => {
    const url = new URL(route.request().url());
    seenCapacity = url.searchParams.get('capacity') ?? '';
    const result = browseResponse('Hikaru', [{
      id_hex: WHALE_ID,
      label: 'Nakamura, Hikaru',
      tier: 2,
      type: 'Chess_Player',
      matched_name_id_hex: '22222222222222222222222222222222',
      match_kind: 'name',
      rating: 1500,
      rd: 120,
      eff_mu: 1260,
      witnesses: 42,
    }]);
    result.receipt.candidate_names = Number(seenCapacity || 2048);
    result.receipt.candidate_capacity = Number(seenCapacity || 2048);
    result.receipt.candidate_truncated = true;
    await route.fulfill({ contentType: 'application/json', body: JSON.stringify(result) });
  });

  await page.goto('/explore?q=Hikaru');
  await expect(page.getByRole('button', { name: 'Expand frontier to 4,096' })).toBeVisible();
  await page.getByRole('button', { name: 'Expand frontier to 4,096' }).click();
  await expect(page).toHaveURL(/capacity=4096/);
  await expect.poll(() => seenCapacity).toBe('4096');
});

test('Browse does not promote an absent surface to an entity result', async ({ page }) => {
  await page.route('**/v1/explore/browse?**', async (route) => {
    await route.fulfill({
      contentType: 'application/json',
      body: JSON.stringify(browseResponse('unheld', [])),
    });
  });

  await page.goto('/explore?q=unheld');
  await expect(page.getByText('No admitted entity or containing structure was reached by this browse program.')).toBeVisible();
  await expect(page.getByRole('link', { name: 'Open its structural neighborhood ›' })).toHaveCount(0);
});

test('legacy free-form resolve routes through Browse', async ({ page }) => {
  await page.goto('/explore/resolve/Sodium%20Chloride');
  await expect(page).toHaveURL('/explore?q=Sodium+Chloride');
});

test('Not-found decomposition keeps tier-2 word constituents visible', async ({ page }) => {
  const sodiumId = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';
  const chlorideId = 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb';
  await page.route('**/v1/explore/notfound?**', async (route) => {
    await route.fulfill({
      contentType: 'application/json',
      body: JSON.stringify({
        reference: 'Sodium Chloride',
        word_id_hex: 'cccccccccccccccccccccccccccccccc',
        exists: false,
        coord: [0, 0, 0, 1],
        decomposition: [
          { ordinal: 0, id_hex: 'dddddddddddddddddddddddddddddddd', label: 'S', tier: 0, text_offset: 0, text_length: 1 },
          { ordinal: 1, id_hex: sodiumId, label: 'Sodium', tier: 2, text_offset: 0, text_length: 6 },
          { ordinal: 2, id_hex: chlorideId, label: 'Chloride', tier: 2, text_offset: 7, text_length: 8 },
          { ordinal: 3, id_hex: 'cccccccccccccccccccccccccccccccc', label: 'Sodium Chloride', tier: 3, text_offset: 0, text_length: 15 },
        ],
        neighbors: [],
        suggestions: [],
        did_you_mean: null,
      }),
    });
  });

  await page.goto('/explore/notfound/Sodium%20Chloride');
  await expect(page.getByRole('link', { name: 'Sodium', exact: true })).toHaveAttribute('href', '/explore/resolve/Sodium');
  await expect(page.getByRole('link', { name: 'Chloride', exact: true })).toHaveAttribute('href', '/explore/resolve/Chloride');
  await expect(page.getByRole('link', { name: 'Sodium Chloride', exact: true })).toHaveCount(0);
});

test('Glome canvas mounts after unlock', async ({ page }) => {
  await page.goto(`/explore/entity/${WHALE_ID}`);
  await expect(page.getByRole('heading', { level: 2 }).first()).toBeVisible({ timeout: 15_000 });
  await page.getByRole('button', { name: 'glome' }).click();
  await page.getByRole('button', { name: /Unlock \(nn\)/ }).click();
  const panes = page.locator('canvas');
  await expect(panes).toHaveCount(2, { timeout: 15_000 });
  await expect(panes.first()).toBeVisible();
  await expect(panes.nth(1)).toBeVisible();
});

test('Gated expand shows GatePrompt when billing bypass is off', async ({ page }) => {
  test.skip(process.env.LAPLACE_BILLING_BYPASS !== 'false', 'requires an endpoint with LAPLACE_BILLING_BYPASS=false');
  await page.goto(`/explore/entity/${WHALE_ID}`);
  await expect(page.getByRole('button', { name: /Unlock \(inspect\)/ })).toBeVisible({ timeout: 15_000 });
  await expect(page.getByText(/inspect/i)).toBeVisible();
});
