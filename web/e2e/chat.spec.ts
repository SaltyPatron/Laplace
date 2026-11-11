import { test, expect } from '@playwright/test';
import { stubApi } from './fixtures/api';

const ASK_PLACEHOLDER = 'Ask about anything the witnesses attest to…';

test.beforeEach(async ({ page }) => { await stubApi(page); });

// There is no separate home page: the address root opens Look up inside the one shell.
test('SPA loads into Look up inside the shell', async ({ page }) => {
  await page.goto('/');
  await expect(page).toHaveURL(/\/explore$/);
  const rail = page.getByRole('navigation', { name: 'Primary navigation' }).first();
  await expect(rail.getByRole('link', { name: 'Laplace home' })).toBeVisible();
  await expect(rail.getByRole('link', { name: 'Look up', exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Search (Ctrl+K)' })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Knowledge', level: 2 })).toBeVisible({ timeout: 15_000 });
});

test('the Ask rail link reaches chat', async ({ page }) => {
  await page.goto('/');
  await page.getByRole('link', { name: 'Ask', exact: true }).click();
  await expect(page).toHaveURL(/\/chat$/);
  await expect(page.getByPlaceholder(ASK_PLACEHOLDER)).toBeVisible({ timeout: 15_000 });
});

test('chat happy path returns a grounded reply', async ({ page }) => {
  const requests: { messages: { role: string; content: string }[] }[] = [];
  await page.route('**/v1/chat/completions', async (route) => {
    requests.push(route.request().postDataJSON());
    // OpenAI-style frames; a completed answer carries finish_reason on choice 0 before [DONE].
    const frame = (choice: object) => `data: ${JSON.stringify({ choices: [{ index: 0, ...choice }] })}\n\n`;
    await route.fulfill({
      contentType: 'text/event-stream',
      body: frame({ delta: { content: 'A whale is a large marine mammal.' }, finish_reason: null })
        + frame({ delta: {}, finish_reason: 'stop' }) + 'data: [DONE]\n\n',
    });
  });

  await page.goto('/chat');
  const input = page.getByPlaceholder(ASK_PLACEHOLDER);
  await input.fill('define whale');
  await page.getByRole('button', { name: 'Send' }).click();

  await expect(page.getByText('define whale')).toBeVisible();
  await expect(page.getByText('A whale is a large marine mammal.')).toBeVisible({ timeout: 20_000 });
  // Only the new turn is sent; continuity is the session key, never resent history.
  expect(requests).toHaveLength(1);
  expect(requests[0].messages).toEqual([{ role: 'user', content: 'define whale' }]);
  // Send clears the input, and an empty input keeps the button disabled by design.
  // "Ready for the next turn" = the busy label ('…') is gone AND a refilled input
  // re-enables the button.
  await expect(page.getByRole('button', { name: 'Send' })).toBeVisible({ timeout: 20_000 });
  await input.fill('next question');
  await expect(page.getByRole('button', { name: 'Send' })).toBeEnabled();
});

test('evidence lookup renders receipts', async ({ page }) => {
  const whaleId = '11111111111111111111111111111111';
  await page.route('**/v1/evidence/whale?**', (route) => route.fulfill({
    json: { entity_id: whaleId, entity_label: 'whale', evidence: [] },
  }));
  await page.goto('/chat');
  const panel = page.getByRole('heading', { name: 'Evidence' }).locator('..').locator('..');
  await panel.getByPlaceholder('word or entity id').fill('whale');
  await panel.getByRole('button', { name: 'Look up' }).click();

  await expect(panel.getByRole('heading', { level: 3, name: 'whale' })).toBeVisible();
  await expect(panel.getByText(whaleId, { exact: true })).toBeVisible();
  await expect(panel.getByText('No provenance-backed claims yet.')).toBeVisible();
});

// The topic page was folded into Look up; saved topic links open the same search.
test('a saved topic link opens the knowledge search for that subject', async ({ page }) => {
  await page.goto('/topic/entropy');
  await expect(page).toHaveURL(/\/explore\?q=entropy$/, { timeout: 15_000 });
  await expect(page.getByRole('textbox', { name: 'Search knowledge' })).toHaveValue('entropy');
});

test('billing page lists the plans with a subscribe action each', async ({ page }) => {
  const plan = (id: string, name: string, cents: number) => ({
    plan_id: id, service_id: 'plan', name, description: `${name} plan`, monthly_price_cents: cents, currency: 'usd',
    monthly_credits: { chat: 100 }, included_product_ids: [], support_tier: 'standard', active: true,
  });
  await page.route('**/v1/billing/plans', (route) => route.fulfill({ json: {
    object: 'list', billing_enforced: false,
    data: [plan('starter', 'Starter', 1000), plan('team', 'Team', 5000), plan('enterprise', 'Enterprise', 20000)],
  } }));
  await page.goto('/billing');
  await expect(page.getByRole('heading', { name: 'Plans', exact: true })).toBeVisible({ timeout: 15_000 });
  for (const name of ['Starter', 'Team', 'Enterprise']) {
    await expect(page.getByRole('heading', { name, level: 3 })).toBeVisible();
  }
  await expect(page.getByRole('button', { name: /^Subscribe/ })).toHaveCount(3, { timeout: 10_000 });
});

// The API's own 404 contract needs a real endpoint behind the dev server's /v1 proxy.
test('unknown api route stays JSON', async ({ request }) => {
  test.skip(!process.env.LAPLACE_E2E_LIVE, 'requires a live API endpoint (set LAPLACE_E2E_LIVE=1)');
  const res = await request.get('/v1/definitely/not/a/route');
  expect(res.status()).toBe(404);
  expect(res.headers()['content-type']).toContain('application/json');
});
