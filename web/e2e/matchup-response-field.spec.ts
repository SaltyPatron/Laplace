import { test, expect } from '@playwright/test';
import field from './fixtures/native-response-field-sample.json' with { type: 'json' };
import lexicon from './fixtures/native-lexicon-readings-sample.json' with { type: 'json' };

// Selected rows from the real Native read on staging, 2026-10-31. The fixture
// includes an incoming UCD definition, Native containment, geometry and enough
// distinct claims to exercise paging without counting paths as meanings.
test('comparison leads with meanings and witnessed relationships, with inspection collapsed', async ({ page }) => {
  const side = (label: string, id: string) => ({
    id, label, record: { confirmed: 0, contested: 0, refuted: 0, thin: 1 }, top_facts: [],
  });
  await page.route('**/v1/auth/me', (route) => route.fulfill({ json: { authenticated: false, user: null, providers: [], devPrincipal: 'test' } }));
  await page.route('**/v1/explore/matchup?*', (route) => route.fulfill({ json: {
    x: side('love', '1f6b396352c04d342e21754b7bebc75e'),
    y: side('hate', 'bb706b06e393963d1f85db760ba5040d'), tape: [],
  } }));
  await page.route('**/v1/explore/matchup/verdict?*', (route) => route.fulfill({ json: {
    plane: 'direct witnessed claims', verdict: 'No direct witnessed claim connects these entities.',
  } }));
  await page.route('**/v1/explore/response-field?*', (route) => route.fulfill({ json: { ...field, ...lexicon } }));
  await page.goto('/explore/matchup/love/hate');
  await expect(page.getByRole('heading', { name: 'Meanings and usage', exact: true })).toHaveCount(2);
  await expect(page.getByText(lexicon.readings[0].text, { exact: true })).toBeVisible();
  await expect(page.getByText(lexicon.readings[1].text, { exact: true })).toBeVisible();
  await expect(page.getByText('is antonym of', { exact: true })).toBeVisible();
  await expect(page.getByText('No direct witnessed claim connects these entities.', { exact: true })).not.toBeVisible();
  await expect(page.getByRole('heading', { name: 'Response field', exact: true })).not.toBeVisible();
  await page.locator('summary').filter({ hasText: 'Inspect supporting records' }).click();
  await expect(page.getByRole('heading', { name: 'Response field', exact: true })).toBeVisible();
  await expect(page.getByText('No recorded definitions were found for this entity.')).toHaveCount(0);
  await expect(page.getByText('Page 1 of 2', { exact: true })).toBeVisible();
  const incoming = field.rows.find((row) => row.outbound === false)!;
  const row = page.locator('li').filter({ has: page.getByRole('link', { name: incoming.label, exact: true }) }).first();
  await expect(row).toContainText('←');
  await row.locator('summary').click();
  await expect(row.getByRole('link', { name: 'Recorded claim' })).toHaveAttribute('href', `/explore/entity/${incoming.claim_id}`);
  await expect(row).toContainText('volatility');
  await page.getByRole('button', { name: 'Next', exact: true }).click();
  await expect(page.getByText('Page 2 of 2', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Containment (2)', exact: true }).click();
  await expect(page.getByText('is contained in').first()).toBeVisible();
  await page.getByRole('button', { name: 'Shape (2)', exact: true }).click();
  await expect(page.getByText(/Fréchet/).first()).toBeVisible();
});
