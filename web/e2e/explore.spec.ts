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
  await expect(page.getByRole('link', { name: 'Browse', exact: true })).toBeVisible();
  await expect(page.getByRole('link', { name: 'Warehouse', exact: true })).toBeVisible();

  await page.getByRole('link', { name: 'Warehouse', exact: true }).click();
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

test('Browse routes an unwitnessed surface to its geometric neighborhood', async ({ page }) => {
  await page.route('**/v1/explore/browse?**', async (route) => {
    await route.fulfill({
      contentType: 'application/json',
      body: JSON.stringify(browseResponse('unheld', [])),
    });
  });

  await page.goto('/explore');
  await page.getByRole('textbox', { name: 'Find a starting point in the substrate' }).fill('unheld');
  await page.getByRole('button', { name: 'Browse' }).click();
  await page.getByRole('link', { name: 'Open its structural neighborhood ›' }).click();
  await expect(page).toHaveURL('/explore/notfound/unheld');
});

test('Glome canvas mounts after unlock', async ({ page }) => {
  await page.goto('/explore/resolve/whale');
  await expect(page.getByRole('heading', { level: 2 }).first()).toBeVisible({ timeout: 15_000 });
  // Under the dev billing bypass the entity auto-unlocks (no "Unlock (inspect)" step);
  // the glome tab still gates the nearest-neighbor overlay behind its own prompt.
  await page.getByRole('button', { name: 'glome' }).click();
  await page.getByRole('button', { name: /Unlock \(nn\)/ }).click();
  const panes = page.locator('canvas');
  await expect(panes).toHaveCount(2, { timeout: 15_000 });
  await expect(panes.first()).toBeVisible();
  await expect(panes.nth(1)).toBeVisible();
});

test('Gated expand shows GatePrompt when billing bypass is off', async ({ page }) => {
  // The inspect gate only fires when the endpoint runs with LAPLACE_BILLING_BYPASS=false;
  // under the dev bypass the entity auto-unlocks and this UX is unreachable by design.
  test.skip(process.env.LAPLACE_BILLING_BYPASS !== 'false', 'requires an endpoint with LAPLACE_BILLING_BYPASS=false');
  await page.goto('/explore/resolve/whale');
  await expect(page.getByRole('button', { name: /Unlock \(inspect\)/ })).toBeVisible({ timeout: 15_000 });
  await expect(page.getByText(/inspect/i)).toBeVisible();
});
