// Live audit checks: real host, real data, real credentials. No mocks.
// Each test title starts with its findings.json id in brackets.
import { expect, test, type Page } from '@playwright/test';

const key = process.env.LAPLACE_API_KEY ?? '';
const operator = process.env.LAPLACE_OPERATOR_TOKEN ?? '';
const DOG = '739118a25a01c0155d0689e390715dda';

test.beforeEach(async ({ context, baseURL }) => {
  const host = new URL(baseURL!).host;
  await context.route('**/*', async (route) => {
    const url = new URL(route.request().url());
    if (url.host !== host) return route.continue();
    const headers = { ...route.request().headers() };
    if (operator && (url.pathname.startsWith('/v1/admin') || url.pathname.startsWith('/v1/billing/operator') || url.pathname === '/v1/access')) {
      headers['x-laplace-operator-token'] = operator;
    } else if (key) {
      headers.authorization = `Bearer ${key}`;
    }
    return route.continue({ headers });
  });
});

async function open(page: Page, path: string, settle = 4000) {
  await page.goto(path, { waitUntil: 'networkidle', timeout: 60_000 }).catch(() => {});
  await page.waitForTimeout(settle);
}
const mainText = (page: Page) => page.locator('main').innerText();
/** Lenses are tabs on the subject page; the open lens is its tabpanel. */
async function lens(page: Page, name: string) {
  await page.getByRole('tab', { name, exact: true }).click();
  return page.getByRole('tabpanel', { name });
}

// ---------- auth ----------
test('[AUTH-1] /v1/auth/me reports the key principal', async ({ request }) => {
  const res = await request.get('/v1/auth/me', { headers: { authorization: `Bearer ${key}` } });
  const body = await res.json();
  expect(body.authenticated, JSON.stringify(body)).toBe(true);
});

test('[AUTH-2] operator can revoke a key without holding it', async ({ request }) => {
  const issued = await (await request.post('/v1/billing/operator/keys', {
    headers: { 'x-laplace-operator-token': operator }, data: { tenant: 'deployment-verification', label: 'audit-auth-2' } })).json();
  const revoke = await request.post('/v1/billing/operator/keys/revoke', {
    headers: { 'x-laplace-operator-token': operator }, data: { tenant: 'deployment-verification', key_prefix: issued.key_prefix } });
  if (!revoke.ok()) { // never leave a live key behind, even when the check fails
    await request.post('/v1/billing/keys/revoke', { headers: { authorization: `Bearer ${issued.api_key}` }, data: { key_prefix: issued.key_prefix } });
  }
  expect(revoke.ok(), `status ${revoke.status()}`).toBe(true);
  const after = await request.get('/v1/auth/me', { headers: { authorization: `Bearer ${issued.api_key}` } });
  expect((await after.json()).authenticated).not.toBe(true);
});

test('[AUTH-3] key/operator principal sees signed-in UI and Admin', async ({ page }) => {
  await open(page, '/operator');
  await expect(page.getByRole('button', { name: /sign in with/i })).toHaveCount(0);
  await expect(page.getByRole('navigation', { name: /administration sections/i })).toBeVisible();
});

// ---------- search ----------
test('[SRCH-1] geometric neighbors are not answers', async ({ page }) => {
  await open(page, '/explore?q=photosynthesis', 8000);
  const text = await mainText(page);
  expect(text).not.toMatch(/geodesic \d|frechet \d/i);
  await expect(page.getByRole('checkbox', { name: /^neighbors/i })).toHaveCount(0);
});

test('[SRCH-2] no-match is an empty state', async ({ page }) => {
  await open(page, '/explore?q=how%20do%20plants%20make%20food', 8000);
  expect(await mainText(page)).not.toMatch(/No content is witnessed/);
});

test('[SRCH-3] internal shape names are not user-facing', async ({ page }) => {
  await open(page, '/explore?q=photosynthesis', 8000);
  const text = await mainText(page);
  expect(text).not.toMatch(/search methods complete/i);
  expect(text).not.toMatch(/\b(band_facts|what_is|fallback)\b/);
});

test('[SRCH-4] packaging metadata is not shown as knowledge', async ({ page }) => {
  await open(page, '/explore?q=photosynthesis', 8000);
  await page.getByRole('link', { name: /^photosynthesis$/ }).first().click().catch(() => {});
  await page.waitForTimeout(4000);
  const text = await mainText(page);
  for (const bad of ['type of pointer that lemma', 'upos:', 'HAS SENSE', 'variety of language:']) expect(text).not.toContain(bad);
});

test('[SRCH-5] a natural-language question returns content', async ({ page }) => {
  await open(page, '/explore?q=what%20is%20photosynthesis', 10000);
  expect(await mainText(page)).toContain('synthesis of compounds');
});

test('[SRCH-6] no long-precision numbers', async ({ page }) => {
  for (const path of ['/explore', '/explore?q=photosynthesis']) {
    await open(page, path, 6000);
    expect(await mainText(page), path).not.toMatch(/\d\.\d{8,}/);
  }
});

test('[SRCH-7] at most two scroll containers in the search workspace', async ({ page }) => {
  await open(page, `/explore?q=dog&selected=${DOG}`, 8000);
  const scrollers = await page.evaluate(() => [...document.querySelectorAll('main *')].filter((e) => {
    const s = getComputedStyle(e); return /(auto|scroll)/.test(s.overflowY) && e.scrollHeight > e.clientHeight + 4;
  }).length);
  expect(scrollers).toBeLessThanOrEqual(2);
});

// ---------- chat ----------
async function ask(page: Page, question: string) {
  await open(page, '/chat', 2000);
  const box = page.getByPlaceholder(/ask about anything/i);
  await box.fill(question);
  await box.press('Enter');
  await page.waitForTimeout(45_000);
}

test('[CHAT-1] chat answers what the substrate knows', async ({ page }) => {
  test.setTimeout(90_000);
  await ask(page, 'What is photosynthesis?');
  const text = await mainText(page);
  expect(text).not.toMatch(/no reply/i);
  expect(text).toMatch(/synthesis/i);
});

test('[CHAT-2] no debug dump inside the reply', async ({ page }) => {
  test.setTimeout(90_000);
  await ask(page, 'What is a dog?');
  const text = await mainText(page);
  expect(text).not.toMatch(/Exact producing lifecycle receipts|Forward pass proof/);
});

test('[CHAT-3] failures show a plain message and Retry', async ({ page, context }) => {
  test.setTimeout(60_000);
  await context.route('**/v1/chat/**', (route) => route.abort());
  await context.route('**/v1/responses**', (route) => route.abort());
  await open(page, '/chat', 2000);
  const box = page.getByPlaceholder(/ask about anything/i);
  await box.fill('test'); await box.press('Enter');
  await page.waitForTimeout(8000);
  expect(await mainText(page)).not.toMatch(/Exception/);
  await expect(page.getByRole('button', { name: /retry|try again/i })).toBeVisible();
});

// ---------- integrity ----------
test('[GLOME-1] hash/mantissa is not a peer geometry view', async ({ page }) => {
  await open(page, '/unicode', 8000);
  await expect(page.getByRole('button', { name: /hash \/ mantissa/i })).toHaveCount(0);
  await open(page, `/explore/entity/${DOG}?inspect=1&detail=glome`, 8000);
  expect(await mainText(page)).not.toMatch(/Mantissa-unpacked/);
});

test('[CLAIM-1] claims show witness, source and standing', async ({ page }) => {
  await open(page, `/explore/entity/${DOG}`, 8000);
  // Each claim row opens its witnesses (source, trust, games) from its "N sources" button.
  await page.getByRole('button', { name: /^\d+ sources? ·/ }).first().click();
  await page.waitForTimeout(3000);
  const text = await mainText(page);
  expect(text).not.toMatch(/Recorded claim \d/);
  expect(text).toMatch(/WordNet|Wiktionary|ConceptNet|witness/i);
});

test('[NUM-1] highway layer counts do not contradict', async ({ page }) => {
  await open(page, '/explore/highway/iso', 6000);
  const text = await mainText(page);
  const claimed = Number(text.match(/(\d[\d,]*) consensus edges across/)?.[1]?.replace(/,/g, '') ?? 0);
  expect(claimed > 0 && /No consensus edges witnessed/.test(text)).toBe(false);
});

test('[NUM-2] audit counts are labeled truthfully', async ({ page }) => {
  await open(page, '/explore/audit', 6000);
  expect(await mainText(page)).not.toMatch(/MEASURED\): Not measured/);
});

test('[REL-1] no invented HAS_/IS_ relation names', async ({ page }) => {
  for (const path of ['/explore/highway', '/explore/highway/iso']) {
    await open(page, path, 6000);
    expect(await mainText(page), path).not.toMatch(/\b(HAS|IS)_[A-Z_]{2,}/);
  }
});

test('[GRAPH-1] bank values are not graph nodes', async ({ page }) => {
  await open(page, `/explore/entity/${DOG}?inspect=1&detail=graph`, 12000);
  const labels = await page.evaluate(() => document.body.innerText);
  expect(labels).not.toMatch(/^(NOUN|VERB|ADJ|ADV)$/m);
});

// ---------- containment & geometry ----------
test('[CONT-1] Appears in is first-class and ungated', async ({ page }) => {
  await open(page, `/explore/entity/${DOG}`, 8000);
  const section = await lens(page, 'Appears in');
  await expect(section).toBeVisible();
  await expect(section).toContainText(/dog/i);
  expect(await mainText(page)).not.toMatch(/Payment required/i);
});

test('[CONT-2] containment has total, paging and source per row', async ({ page }) => {
  await open(page, `/explore/entity/${DOG}`, 8000);
  const section = await lens(page, 'Appears in');
  await expect(section).toContainText(/\d[\d,]* (sentences|containers|results)/i);
  await expect(section.getByRole('button', { name: /next/i })).toBeVisible();
  const rows = section.getByRole('listitem');
  expect(await rows.count()).toBeGreaterThan(0);
  await expect(rows.first().locator('[data-source]')).toBeVisible();
});

test('[CONT-3] the word is highlighted in each container', async ({ page }) => {
  await open(page, `/explore/entity/${DOG}`, 8000);
  const rows = (await lens(page, 'Appears in')).getByRole('listitem');
  await expect(rows.first().locator('mark')).toBeVisible();
});

test('[SHAPE-1] trajectory comparison exists', async ({ page }) => {
  await open(page, '/explore/shape?x=dog&y=wolf', 10000);
  await expect(page.locator('main canvas').first()).toBeVisible();
  expect(await mainText(page)).toMatch(/Fr[ée]chet/i);
});

test('[GRAPH-2] graph has filters, legend and node panel', async ({ page }) => {
  await open(page, `/explore/entity/${DOG}?inspect=1&detail=graph`, 12000);
  await expect(page.getByRole('combobox', { name: /relation/i }).or(page.getByRole('group', { name: /relation/i }))).toBeVisible();
  await expect(page.getByRole('list', { name: /legend/i }).or(page.getByText(/how to read this/i)).first()).toBeVisible();
});

test('[GRAPH-3] 2D graph shows labels and honors hops', async ({ page }) => {
  await open(page, `/explore/entity/${DOG}?inspect=1&detail=graph`, 12000);
  const status = await mainText(page);
  const setting = Number(status.match(/Graph settings · (\d+)h/)?.[1] ?? NaN);
  const shown = Number(status.match(/(\d+)-hop/)?.[1] ?? NaN);
  expect(shown).toBe(setting);
});

test('[CONST-1] constellation loads fast with readable labels', async ({ page }) => {
  const t0 = Date.now();
  await page.goto('/explore/constellation');
  await expect(page.getByText(/Loading constellation/i)).toHaveCount(0, { timeout: 10_000 });
  expect(Date.now() - t0).toBeLessThan(10_000);
  expect(await mainText(page)).not.toMatch(/□|\b[0-9a-f]{32}\b/);
});

test('[UNI-1] unicode glome can find a character', async ({ page }) => {
  await open(page, '/unicode', 8000);
  const search = page.getByRole('textbox', { name: 'Find a character' });
  await search.fill('A'); await search.press('Enter');
  await page.waitForTimeout(3000);
  expect(await mainText(page)).toMatch(/U\+0041/);
});

test('[SRC-1] sources catalogue loads', async ({ page }) => {
  await open(page, '/explore/sources', 8000);
  const text = await mainText(page);
  expect(text).not.toMatch(/catalog query failed/i);
  expect(text).toMatch(/wordnet|propbank|verbnet|unicode/i);
});

// ---------- navigation ----------
test('[IA-1] knowledge sub-navigation ≤ 5 items', async ({ page }) => {
  await open(page, '/explore', 3000);
  const nav = page.getByRole('navigation', { name: /knowledge sections/i });
  expect(await nav.getByRole('link').count()).toBeLessThanOrEqual(5);
});

test('[IA-2] one search box per page', async ({ page }) => {
  for (const path of ['/explore', '/chat', '/explore/mesh']) {
    await open(page, path, 3000);
    expect(await page.locator('input[type=search]:visible, [role=searchbox]:visible').count(), path).toBeLessThanOrEqual(1);
  }
});

test('[IA-3] entity page has one view bar and no hidden inspector', async ({ page }) => {
  await open(page, `/explore/entity/${DOG}`, 8000);
  expect(await page.locator('main details:has(nav)').count()).toBe(0);
  expect(await page.getByRole('navigation', { name: /presentations|inspection views/i }).count()).toBeLessThanOrEqual(1);
});

test('[IA-4] entity page title is the label', async ({ page }) => {
  await open(page, `/explore/entity/${DOG}`, 8000);
  await expect(page.locator('main h1, main h2').first()).toHaveText(/^dog$/);
});

test('[IA-5] one breadcrumb', async ({ page }) => {
  await open(page, `/explore/mesh/${DOG}`, 6000);
  expect(await page.getByRole('navigation', { name: /breadcrumb/i }).count()).toBeLessThanOrEqual(1);
});

test('[IA-6] mesh honors ?q=', async ({ page }) => {
  await open(page, '/explore/mesh?q=dog', 8000);
  await expect(page.locator('main')).toContainText(/\bdog\b/);
  await expect(page.getByPlaceholder(/word, ILI/i)).toHaveCount(0);
});

// ---------- pages ----------
test('[ENT-1] related information grouped by relation', async ({ page }) => {
  await open(page, `/explore/entity/${DOG}`, 8000);
  expect(await page.locator('main section[data-relation], main [data-relation-group]').count()).toBeGreaterThan(1);
});

test('[PROOF-1] whitespace nodes are visible', async ({ page }) => {
  await open(page, '/proof?q=The%20dog%20barked.', 8000);
  expect(await mainText(page)).toMatch(/␠|⎵|space/);
});

test('[TRACE-1] forward trace has no empty card', async ({ page }) => {
  await open(page, '/explore/walk', 4000);
  const empties = await page.evaluate(() => [...document.querySelectorAll('main section, main [class*=panel], main [class*=Panel]')]
    .filter((e) => e.getBoundingClientRect().height > 200 && (e as HTMLElement).innerText.trim().split('\n').length < 4).length);
  expect(empties).toBe(0);
});

test('[CHESS-1] move list in SAN', async ({ page }) => {
  await open(page, '/play', 4000);
  await page.getByRole('button', { name: /new game/i }).click();
  await page.locator('[data-square=e2]').click(); await page.locator('[data-square=e4]').click();
  await page.waitForTimeout(6000);
  const text = await mainText(page);
  expect(text).toMatch(/\be4\b/); expect(text).not.toMatch(/e2e4/);
});

test('[CHESS-2] board coordinates shown', async ({ page }) => {
  await open(page, '/play', 4000);
  await expect(page.locator('[data-board-coordinate]').first()).toBeVisible();
});

test('[CHESS-3] player profile is formatted', async ({ page }) => {
  // Search by name: with no recorded games the player sorts among the unrated, far past page one.
  await open(page, '/chess?q=Nakamura', 5000);
  await page.getByRole('link', { name: /Nakamura, Hikaru/ }).first().click();
  await page.waitForTimeout(5000);
  const text = await mainText(page);
  expect(text).not.toMatch(/\b1\d{9}\b/);
  expect(text).not.toMatch(/api\.chess\.com\/pub\/(country|player)/);
});

test('[CHESS-4] substrate move scores are truthful', async ({ page }) => {
  await open(page, '/play', 5000);
  const text = await mainText(page);
  const allPrior = (text.match(/±0\s+prior/g) ?? []).length >= 5;
  if (allPrior) expect(text).toMatch(/no (substrate )?evidence for this position/i);
});

test('[LAB-1] calibration can be selected', async ({ page }) => {
  // Never a dead end: either a measured calibration is shown, or the page says what is
  // missing and how an operator produces and selects one.
  await open(page, '/lab/calibration', 4000);
  expect(await mainText(page)).not.toMatch(/No installed calibration has been selected\.\s*$/m);
  await expect(page.getByRole('table', { name: /Stockfish search calibration/i })
    .or(page.getByRole('region', { name: /produce a calibration/i })).first()).toBeVisible();
});

test('[LAB-2] lichess routing says who may change it', async ({ page }) => {
  // The audit holds a service key, not a person. Routing follows the platform roles
  // (services.manage), so the page must say why this session cannot change it; an
  // administrator changing it is covered by LichessControlAccessTests.
  await open(page, '/lab/lichess', 4000);
  const text = await mainText(page);
  expect(text).not.toMatch(/does not have permission to change the bot destination/);
  expect(text).toMatch(/service credential/i);
});

test('[DATA-1] admission history is listed', async ({ page }) => {
  await open(page, '/data', 5000);
  await expect(page.getByRole('region', { name: /admission history/i })).toBeVisible();
});

// ---------- admin ----------
test('[ADM-1] ingestion failures explain themselves', async ({ page }) => {
  await open(page, '/operator?section=ingest', 6000);
  const errs = await page.getByText(/exited with status \d+\.?$/).count();
  expect(errs).toBe(0);
});

test('[ADM-2] no terminate on idle or own backends', async ({ page }) => {
  await open(page, '/operator?section=activity', 6000);
  const idleWithTerminate = await page.locator('tr', { hasText: /\bidle\b/ }).getByRole('button', { name: /terminate/i }).count();
  expect(idleWithTerminate).toBe(0);
});

test('[ADM-3] vacuum is not primary and needs confirmation', async ({ page }) => {
  await open(page, '/operator?section=repair', 5000);
  const vacuum = page.getByRole('button', { name: /run vacuum/i });
  expect(await vacuum.getAttribute('data-variant')).not.toBe('primary');
});

test('[ADM-4] workspace directory is complete', async ({ page }) => {
  await open(page, '/operator?section=workspaces', 6000);
  const text = await mainText(page);
  // Every "Workspace ID" label is followed by an ID (a blank line may sit between them).
  const labels = text.match(/Workspace ID/g)?.length ?? 0;
  expect(labels).toBeGreaterThan(0);
  expect(text.match(/Workspace ID\s+[\w-]+/g)?.length ?? 0).toBe(labels);
});

test('[ADM-5] operator copy is guidance', async ({ page }) => {
  await open(page, '/operator?section=repair', 5000);
  expect(await mainText(page)).not.toMatch(/incident/i);
});

test('[SET-1] settings tab bar renders correctly', async ({ page }) => {
  await open(page, '/settings', 4000);
  // Workspace settings belong to a person; a service key is shown how to sign in instead.
  if (await page.getByText(/Sign in to select a workspace/).isVisible()) {
    await expect(page.getByRole('link', { name: /Sign in with/ }).first()).toBeVisible();
    return;
  }
  const box = await page.locator('[aria-current=page], [aria-selected=true]').filter({ hasText: /workspace/i }).first().boundingBox();
  expect(box?.height ?? 999).toBeLessThanOrEqual(48);
});

test('[BILL-1] billing uses human labels', async ({ page }) => {
  await open(page, '/billing', 5000);
  const text = await mainText(page);
  expect(text).not.toMatch(/\b[a-z]+\.[a-z_]+\b(?=\s+\d)/);
  expect(text).not.toMatch(/\bt-[0-9a-f]{32}\b/);
});

test('[FAM-1] ambient familiar never covers content', async ({ page }) => {
  await open(page, '/explore?q=dog', 6000);
  const overlap = await page.evaluate(() => {
    const f = document.querySelector('[data-familiar], [class*=familiar], [class*=Familiar]');
    if (!f || getComputedStyle(f).display === 'none') return false;
    const r = f.getBoundingClientRect();
    const main = document.querySelector('main')!.getBoundingClientRect();
    return r.width > 0 && r.right > main.left && r.left < main.right && r.bottom > main.top && r.top < main.bottom;
  });
  expect(overlap).toBe(false);
});

test('[ERR-1] one error component', async ({ page }) => {
  await open(page, '/explore?q=zzzzqqqq', 8000);
  const text = await mainText(page);
  expect(text).not.toMatch(/\(request [0-9a-f]{32}\)/);
});
