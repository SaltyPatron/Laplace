// Lens system on the entity contract: drawer, lens tabs, relation grouping and direction, witnesses, states,
// unavailable facets, re-centring and Escape. Fixture responses use the real explore API shapes.
import { chromium } from '@playwright/test';
import { createServer } from 'vite';
const server = await createServer({ server: { host: '127.0.0.1', port: 0, strictPort: true }, logLevel: 'error' });
await server.listen();
const base = server.resolvedUrls.local[0].replace(/\/$/, '');
const DOG = '739118a25a01c0155d0689e390715dda';
const id = (n) => n.toString(16).padStart(32, '0');
const entity = {
  id_hex: DOG, label: 'dog', tier: 1, type: 'text', exists: true, evidence_count: 4,
  physicalities: [{ x: 0.1, y: 0.2, z: 0.3, m: 0.4, radius: 0.9, n_constituents: 3 }], salient_facts: [], senses: [],
  constituents: [['d', 1], ['o', 2], ['g', 3]].map(([l, o]) => ({ ordinal: o, child_id_hex: id(o), child_label: l, run_length: 1, flags: 0 })),
  packed_vertices: [], realized_vertices: [],
  consensus_out: [
    { direction: 'out', type: 'IsA', entity_id_hex: id(10), entity_label: 'canine', eff_mu: 1612.4, witnesses: 3 },
    { direction: 'out', type: 'IsA', entity_id_hex: id(11), entity_label: 'domestic animal', eff_mu: 1087.071, witnesses: 1 },
    { direction: 'out', type: 'AtLocation', entity_id_hex: id(12), entity_label: 'backyard', eff_mu: 1087.071, witnesses: 1 },
  ],
  consensus_in: [{ direction: 'in', type: 'IsA', entity_id_hex: id(13), entity_label: 'puppy', eff_mu: 1210.5, witnesses: 2 }],
  evidence: [
    { type_id: null, type_label: 'IsA', object_id: id(10), object_label: 'canine', source_id: id(100), source_label: 'princeton-wordnet', outcome: 1, observation_count: 1, games: 2, trust: 0.9, score: 1 },
    { type_id: null, type_label: 'IsA', object_id: id(10), object_label: 'canine', source_id: id(101), source_label: 'open-english-wordnet', outcome: 1, observation_count: 1, games: 1, trust: 0.85, score: 1 },
    { type_id: null, type_label: 'AtLocation', object_id: id(12), object_label: 'backyard', source_id: id(102), source_label: 'ConceptNet', outcome: 1, observation_count: 1, games: 1, trust: 0.35, score: 1 },
  ],
};
const containers = { containers: { id_hex: DOG, containers: [
  { entity_id_hex: id(200), entity_label: 'The bear was in front of this woman … in the attitude of a begging dog.', tier: 2, type: 'text', hops: 1 },
  { entity_id_hex: id(201), entity_label: 'PersonX tries to save a dog', tier: 2, type: 'text', hops: 1 },
  { entity_id_hex: id(202), entity_label: '[[golden retrievers]] are [[a breed of dog]]', tier: 2, type: 'text', hops: 1 },
] } };
const neighbors = { neighbors: { id_hex: DOG, semantic: [], structural: [
  { neighbor: 'dot', geodesic: 0.002, frechet: 0.21, axis: 'all', neighbor_id_hex: id(300) },
  { neighbor: 'dig', geodesic: 0.003, frechet: 0.25, axis: 'all', neighbor_id_hex: id(301) },
] } };

const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
await page.route('**/v1/**', (route) => {
  const p = new URL(route.request().url()).pathname;
  if (p === '/v1/auth/me') return route.fulfill({ json: { authenticated: false, user: null, providers: [] } });
  if (p.startsWith('/v1/entities/')) return route.fulfill({ status: 404, json: { error: { message: 'not served' } } });
  if (p === `/v1/explore/entities/${DOG}`) return route.fulfill({ json: { id: 'x', object: 'explore.entity', created: 0, entity } });
  if (p.endsWith('/containers')) return route.fulfill({ json: containers });
  if (p.endsWith('/neighbors')) return route.fulfill({ json: neighbors });
  return route.fulfill({ status: 503, json: { error: { message: 'fixture' } } });
});
await page.route('**/health/**', (r) => r.fulfill({ json: { status: 'ok' } }));
const results = [];
const check = async (name, fn) => { try { await fn(); results.push(`PASS ${name}`); } catch (e) { results.push(`FAIL ${name}: ${e.message.split('\n')[0]}`); } };
await page.goto(`${base}/explore?peek=${DOG}`);
await page.waitForTimeout(2500);
await check('drawer opens with label', async () => { await page.getByRole('complementary', { name: /Inspector: dog/ }).waitFor({ timeout: 5000 }); });
await check('means groups by relation', async () => { await page.getByRole('heading', { name: /dog is a …/ }).waitFor({ timeout: 5000 }); await page.getByRole('heading', { name: /… is a dog/ }).waitFor({ timeout: 5000 }); });
await check('witnesses expand with sources', async () => {
  await page.getByRole('button', { name: /2 sources · 3 games/ }).click();
  await page.getByText('princeton-wordnet').waitFor({ timeout: 3000 });
});
await check('thin vs confirmed states', async () => { await page.getByText('Confirmed').first().waitFor(); await page.getByText('Thin evidence').first().waitFor(); });
await check('appears-in lens lists sentences', async () => { await page.getByRole('tab', { name: 'Appears in' }).click(); await page.getByText('PersonX tries to save a dog').waitFor({ timeout: 5000 }); });
await check('made-of lens', async () => { await page.getByRole('tab', { name: 'Made of' }).click(); await page.getByRole('button', { name: 'o', exact: true }).waitFor({ timeout: 5000 }); });
await check('next-to says not deployed', async () => { await page.getByRole('tab', { name: 'Next to' }).click(); await page.getByText(/not deployed yet/).waitFor({ timeout: 5000 }); });
await check('chip re-centres drawer', async () => {
  await page.getByRole('tab', { name: 'Appears in' }).click();
  await page.getByRole('button', { name: /PersonX tries to save a dog/ }).click();
  await page.waitForURL(/peek=0+c9/, { timeout: 5000 });
});
await check('escape closes drawer', async () => { await page.keyboard.press('Escape'); await page.waitForFunction(() => !document.querySelector('[data-inspector]'), null, { timeout: 3000 }); });
await browser.close();
await server.close();
console.log(results.join('\n'));
if (results.some((r) => r.startsWith('FAIL'))) process.exit(1);
console.log('LENSES_UI_OK');

