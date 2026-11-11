import { test, expect } from '@playwright/test';
import { stubApi } from './fixtures/api';
import { entityResponse } from './fixtures/entity';

const id = '11111111111111111111111111111111';
const target = '33333333333333333333333333333333';
const kind = '44444444444444444444444444444444';

const sediment = entityResponse({
  id_hex: id, label: 'sediment', tier: 1,
  consensus_out: [
    { type: 'AtLocation', entity_id_hex: target, entity_label: 'bottom of sea', eff_mu: 1620, witnesses: 3 },
    { type: 'IsA', entity_id_hex: kind, entity_label: 'matter', eff_mu: 1710, witnesses: 5 },
  ],
});

test('entity page leads with meanings, with relations in words and subjects that open in the inspector', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 700 });
  await stubApi(page);
  await page.route(`**/v1/explore/entities/${id}`, (route) => route.fulfill({ json: sediment }));
  await page.goto(`/explore/entity/${id}`);

  await expect(page.getByRole('heading', { name: 'sediment', exact: true, level: 1 })).toBeVisible({ timeout: 15_000 });
  await expect(page.getByRole('tab', { name: 'Means', exact: true })).toHaveAttribute('aria-selected', 'true');
  const means = page.getByRole('tabpanel', { name: 'Means' });
  // Relation names read as words, not identifiers.
  await expect(means.getByText('sediment at location …')).toBeVisible();
  await expect(means.getByText('sediment is a …')).toBeVisible();
  await expect(means.getByText(/AtLocation|IsA/)).toHaveCount(0);

  // A related subject opens in the inspector without leaving the page.
  await means.getByRole('button', { name: 'bottom of sea', exact: true }).click();
  await expect(page).toHaveURL(new RegExp(`/explore/entity/${id}\\?.*peek=${target}`));
});

test('a failed entity read keeps the page and offers a retry', async ({ page }) => {
  await stubApi(page);
  let fail = true;
  await page.route(`**/v1/explore/entities/${id}`, (route) => fail
    ? route.fulfill({ status: 503, json: { error: { message: 'Inspection temporarily unavailable' } } })
    : route.fulfill({ json: sediment }));
  await page.goto(`/explore/entity/${id}`);

  const means = page.getByRole('tabpanel', { name: 'Means' });
  await expect(means.getByText('Inspection temporarily unavailable', { exact: false })).toBeVisible({ timeout: 15_000 });
  await expect(page.getByRole('tab', { name: 'Made of', exact: true })).toBeVisible();
  fail = false;
  await means.getByRole('button', { name: 'Try again' }).click();
  await expect(means.getByText('sediment at location …')).toBeVisible();
});
