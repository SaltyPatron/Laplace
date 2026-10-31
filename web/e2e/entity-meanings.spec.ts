import { test, expect } from '@playwright/test';

const id = '11111111111111111111111111111111';
const claim = '22222222222222222222222222222222';
const target = '33333333333333333333333333333333';

test('entity browse leads with meanings and readable facts and fetches inspection on demand', async ({ page }) => {
  const inspection: string[] = [];
  await page.route('**/v1/auth/me', route => route.fulfill({ json: { authenticated: false, user: null, providers: [], devPrincipal: 'test' } }));
  await page.route(`**/v1/explore/entities/${id}/preview`, route => route.fulfill({ json: {
    id_hex: id, label: 'sediment', type: '', tier: 1, exists: true, evidence_count: 21, preview_facts: [],
  } }));
  await page.route('**/v1/explore/lexicon?*', route => route.fulfill({ json: {
    object: 'lexicon', rounds: 1, fanout: 0, nearest: 0,
    readings: [{ side: 1, text: 'matter that has been deposited by some natural process', relation: 'definition', via: 'sediment', claim_ids: [claim], witnesses: 1, standing: 1087 }],
    connections: [{ relation: 'at location', claim_id: claim, from: 'sediment', to: 'bottom of sea', from_id: id, to_id: target, witnesses: 1 }],
    rows: [{ claim_id: claim, outbound: true }],
  } }));
  await page.route(`**/v1/explore/entities/${id}`, route => {
    inspection.push(route.request().url());
    return route.fulfill({ status: 503, json: { error: { message: 'Inspection temporarily unavailable' } } });
  });
  const standing: string[] = [];
  page.on('request', request => { if (request.url().endsWith('/record')) standing.push(request.url()); });
  await page.goto(`/explore/entity/${id}`);
  await expect(page.getByRole('heading', { name: 'sediment', exact: true })).toBeVisible();
  await expect(page.getByText('matter that has been deposited by some natural process', { exact: true })).toBeVisible();
  await expect(page.getByText('at location', { exact: true })).toBeVisible();
  await expect(page.getByRole('link', { name: 'bottom of sea', exact: true })).toHaveAttribute('href', `/explore/entity/${target}`);
  await expect(page.getByRole('heading', { name: 'Salient facts', exact: true })).toHaveCount(0);
  await expect(page.getByText('Composition tier 1', { exact: true })).not.toBeVisible();
  await expect(page.getByRole('button', { name: 'overview', exact: true })).not.toBeVisible();
  expect(inspection).toHaveLength(0);
  expect(standing).toHaveLength(0);
  await page.getByText('Inspect source records', { exact: true }).click();
  await expect.poll(() => inspection.length).toBe(1);
  // A failed inspection must not replace the readable primary answer.
  await expect(page.getByText('matter that has been deposited by some natural process', { exact: true })).toBeVisible();
});
