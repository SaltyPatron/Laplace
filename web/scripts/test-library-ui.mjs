// Browser interaction fixtures isolate UI behavior; no installed substrate is used.
import assert from 'node:assert/strict';
import { createServer } from 'vite';
import { chromium, expect } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { mkdir, writeFile } from 'node:fs/promises';
import { resolve } from 'node:path';

const output = resolve(process.env.LAPLACE_LIBRARY_UI_EVIDENCE || '../../artifacts/library-ui');
await mkdir(output, { recursive: true });
const server = await createServer({ server: { host: '127.0.0.1', port: 0, strictPort: true } });
const ids = ['a'.repeat(32), 'b'.repeat(32), 'c'.repeat(32), 'd'.repeat(32)];
const sources = [
  { key: 'Source A', stage: 'language', layer: 'lexical', role: 'lexicon', id_hex: ids[0], evidence: 10, content: 20 },
  { key: 'Source B', stage: 'reference', layer: 'lexical', role: 'reference', id_hex: ids[1], evidence: 5, content: 8 },
  { key: 'Source C', stage: 'reference', layer: 'corpus', role: 'reference', id_hex: ids[2], evidence: null, content: null },
  { key: 'Source A', stage: 'reference', layer: 'corpus', role: 'reference', id_hex: ids[3], evidence: 30, content: 40 },
];
const catalog = { sources, stages: [], counts: [], featured_refs: [], top_relations: [], consensus: { consensusRows: 0 } };
const hits = ids.slice(0, 2).map((id, index) => ({ id_hex: id, label: `Entity ${index + 1}`, tier: 2, type: 'noun', match_kind: 'name', witnesses: 2, rating: 1500, rd: 50, eff_mu: 1400 }));
const gameId = 'f'.repeat(32), positionId = '1'.repeat(32);
const unresolvedId = '7'.repeat(32);
const startFen = 'rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1';
let browser;
let passed = false;
const errors = [], checks = [], accessibility = [];
const mark = name => { checks.push(name); console.log('PASS ' + name); };
try {
  await server.listen();
  browser = await chromium.launch({ headless: true });
  const context = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
  const page = await context.newPage();
  page.on('pageerror', error => errors.push(error.message));
  let failCatalog = false, browseReads = 0, previewReads = 0, entityReads = 0, resolveReads = 0;
  const queryReads = [], comparisonReads = [];
  await page.route('**/health/**', route => route.fulfill({ json: { ready: true } }));
  // Recorded games are read from the modular host, not the monorepo API.
  await page.route('**/modular/api/chess/game?**', route => new URL(route.request().url()).searchParams.get('id') === gameId
    ? route.fulfill({ json: { id: gameId, line: null, tags: [{ name: 'Event', value: 'Fixture event' }], white: 'Fixture player', black: 'Fixture opponent', result: '1-0', date: '2026.01.01', event: 'Fixture event', eco: 'A00', start_fen: startFen, movetext: '1. e4', complete: true, plies: [{ ply: 1, san: 'e4', uci: 'e2e4', fen: 'rnbqkbnr/pppppppp/8/8/4P3/8/PPPP1PPP/RNBQKBNR b KQkq e3 0 1', white_moved: true, position: positionId, clock: null }] } })
    : route.fulfill({ status: 400, json: { error: 'unknown game' } }));
  await page.route('**/v1/**', route => {
    const url = new URL(route.request().url()), path = url.pathname;
    if (path === '/v1/auth/me') return route.fulfill({ json: { authenticated: false, user: null, providers: [] } });
    if (path === '/v1/query/shapes') return route.fulfill({ json: { shapes: [
      ...['define', 'describe', 'translate', 'band_facts', 'generate'].map(shape => ({ shape, summary: shape + ' fixture', needs_topic2: false, needs_type: false, accepts_lang: shape === 'translate' })),
      { shape: 'beam', summary: 'beam fixture', needs_topic2: false, needs_type: true },
      { shape: 'path', summary: 'path fixture', needs_topic2: true, needs_type: false },
      { shape: 'related', summary: 'relation fixture', needs_topic2: false, needs_type: true },
    ] } });
    if (path === '/v1/query/bands') return route.fulfill({ json: { bands: [{ band: 1, name: 'identity', consensus_rows: 12 }, { band: 2, name: 'relation', consensus_rows: 8 }] } });
    if (path === '/v1/query/relations') return route.fulfill({ json: { relations: [{ name: 'IS_A', id_hex: ids[3] }] } });
    if (path === '/v1/query/leaders/home') return route.fulfill({ json: { bands: [{ band: 1, name: 'identity', rows: [{ subject_id: ids[0], subject: 'Whale', relation: 'IS_A', object_id: ids[1], object: 'mammal', eff_mu: 1200, witnesses: 2 }], claims: [] }] } });
    if (path === '/v1/query') {
      const body = route.request().postDataJSON(); queryReads.push(body);
      if (body.shape === 'translate') return route.fulfill({ status: 503, json: { error: { message: 'Translation fixture unavailable' } } });
      return route.fulfill({ json: { object: 'query.result', shape: body.shape, topic_id: ids[0], topic_label: 'Entity 1', rows: [{ reply: 'Fixture combined answer.', eff_mu: body.shape === 'define' ? 1200 : 1300, witnesses: 2 }] } });
    }
    if (path === '/v1/explore/catalog') return route.fulfill(failCatalog ? { status: 503, json: { error: { message: 'Catalogue fixture unavailable' } } } : { json: catalog });
    if (path === '/v1/explore/resolve') { resolveReads++; return route.fulfill({ json: { id_hex: unresolvedId, exists: false } }); }
    if (path === '/v1/explore/notfound') return route.fulfill({ json: { reference: url.searchParams.get('reference'), word_id_hex: unresolvedId, exists: false, coord: [0, 0, 0, 0], decomposition: [], neighbors: [], suggestions: [] } });
    if (/\/sources\/[^/]+\/roster$/.test(path)) return route.fulfill({ json: { rows: [{ subject_id: ids[0], subject: 'Fixture subject', relation: 'is a', object_id: ids[1], object: 'Fixture object', observations: 3 }] } });
    if (path === '/v1/explore/browse') {
      browseReads++;
      return route.fulfill({ json: { object: 'browse', query: url.searchParams.get('q'), hits, receipt: { query_root_id_hex: ids[0], query_member_ids_hex: [], matched_entities: 2, candidate_names: 2, candidate_capacity: 2048, candidate_truncated: false, returned: 2, elapsed_us: 10 } } });
    }
    if (path.endsWith('/taxonomy')) return route.fulfill({ json: { up: [{ id: ids[1], label: 'Broader fixture class', eff_mu: 1200 }], children: [] } });
    if (path.endsWith('/mesh')) {
      const id = path.split('/').at(-2);
      return route.fulfill({ json: { id, label: id === ids[2] ? null : 'Fixture relation subject', belongs_to: [{ id: ids[1], label: 'Fixture related parent', relation: 'IS_A', witnesses: 2 }], roster: [{ id: ids[2], label: null, relation: 'MEMBER_OF', witnesses: 1 }] } });
    }
    if (path === '/v1/explore/matchup') {
      comparisonReads.push([url.searchParams.get('x'), url.searchParams.get('y')]);
      const side = (id, label) => ({ id, label, record: { thin: 0, confirmed: 1, contested: 0, refuted: 0 }, top_facts: [] });
      return route.fulfill({ json: { x: side(ids[0], 'Comparison entity A'), y: side(ids[1], 'Comparison entity B'), tape: [] } });
    }
    if (path === '/v1/explore/matchup/verdict') return route.fulfill({ json: {} });
    if (path.endsWith('/preview')) {
      previewReads++;
      const id = path.split('/').at(-2), index = ids.indexOf(id);
      return route.fulfill({ json: { id_hex: id, label: index < 0 ? id : `Entity ${index + 1}`, tier: 2, type: 'noun', exists: id !== unresolvedId, evidence_count: 2, preview_facts: [] } });
    }
    if (/^\/v1\/explore\/entities\/[a-f0-9]{32}$/.test(path)) { entityReads++; return route.fulfill({ json: { entity: {
      id_hex: path.split('/').at(-1), label: 'Fixture entity detail', type: 'noun', exists: path.split('/').at(-1) !== unresolvedId, tier: 2,
      physicalities: [], salient_facts: [], senses: [], constituents: [], packed_vertices: [], realized_vertices: [],
      consensus_out: [{ direction: 'out', type: 'IsA', entity_id_hex: ids[1], entity_label: 'Entity 2', eff_mu: 1600, witnesses: 2 }], consensus_in: [],
      evidence: [{ type_id: null, type_label: 'IsA', object_id: ids[1], object_label: 'Entity 2', source_id: ids[3], source_label: 'Fixture source', outcome: 1, observation_count: 1, games: 2, trust: 0.9, score: 1 }],
    } } }); }
    if (path === '/v1/explore/lexicon' && url.searchParams.get('x') === unresolvedId) return route.fulfill({ json: { readings: [], connections: [] } });
    if (path === '/v1/explore/lexicon') return route.fulfill({ json: {
      readings: [{ text: 'Fixture definition for the selected record.', claim_ids: [], witnesses: 1 }],
      connections: [{ from: 'Entity 1', from_id: ids[0], relation: 'is a', to: 'Entity 2', to_id: ids[1], claim_id: ids[2] }],
    } });
    return route.fulfill({ status: 503, json: { error: { message: 'Optional fixture read unavailable' } } });
  });
  const base = server.resolvedUrls.local[0];
  await page.goto(base + 'explore/sources');
  const primary = page.getByRole('navigation', { name: 'Primary navigation' });
  // One rail: destinations, with the current destination's pages under it (no stacked tab bars).
  for (const label of ['Look up', 'Compare', 'Explore', 'Ask', 'My data', 'Chess']) {
    await expect(primary.getByRole('link', { name: label, exact: true })).toBeVisible();
  }
  for (const label of ['Search', 'Sources', 'Identifiers']) await expect(primary.getByRole('link', { name: label, exact: true })).toBeVisible();
  await expect(primary.getByRole('link', { name: 'Sources', exact: true })).toHaveAttribute('aria-current', 'page');
  await expect(page.getByRole('navigation', { name: 'Knowledge sections', exact: true })).toHaveCount(0);
  await expect(page.getByRole('navigation', { name: 'Views and operations' })).toHaveCount(0);
  await expect(page.locator('[data-collection-master] tbody tr')).toHaveCount(4);
  await page.getByRole('group', { name: 'Stage', exact: true }).getByRole('checkbox', { name: 'language', exact: false }).click();
  await expect(page.getByRole('group', { name: 'Stage', exact: true }).getByRole('checkbox', { name: 'language', exact: false })).toBeChecked();
  await expect(page.locator('[data-collection-master] tbody tr')).toHaveCount(1);
  // OR within a facet, AND across facets, including keyboard activation.
  const reference = page.getByRole('group', { name: 'Stage', exact: true }).getByRole('checkbox', { name: 'reference', exact: false });
  await reference.focus(); await page.keyboard.press('Space');
  await expect(page.locator('[data-collection-master] tbody tr')).toHaveCount(4);
  await page.getByRole('group', { name: 'Layer', exact: true }).getByRole('checkbox', { name: 'lexical', exact: false }).click();
  await expect(page.locator('[data-collection-master] tbody tr')).toHaveCount(2);
  await page.locator('[data-collection-master]').getByRole('link', { name: 'Source A', exact: true }).click();
  await expect(page.locator('[data-collection-detail]')).toContainText('Assertions');
  await expect(page.locator('[data-collection-detail]')).toContainText('Fixture subject');
  const selectedAddress = page.url();
  await page.reload();
  await expect(page.locator('[data-collection-master] tbody tr')).toHaveCount(2);
  await expect(page.locator('[data-collection-detail]')).toContainText('Source A');
  assert.equal(page.url(), selectedAddress);
  await page.locator('[data-collection-master]').getByRole('link', { name: 'Source B', exact: true }).click();
  await expect(page.locator('[data-collection-detail]')).toContainText('Source B');
  await page.goBack(); await expect(page.locator('[data-collection-detail]')).toContainText('Source A');
  await page.getByRole('button', { name: 'Clear filters', exact: true }).click();
  await page.getByRole('group', { name: 'Stage', exact: true }).getByRole('checkbox', { name: 'reference', exact: false }).click();
  await page.getByRole('group', { name: 'Layer', exact: true }).getByRole('checkbox', { name: 'corpus', exact: false }).click();
  await expect(page.locator('[data-collection-master] tbody tr')).toHaveCount(2);
  await page.locator('[data-collection-master]').getByRole('link', { name: 'Source A', exact: true }).click();
  await expect(page.locator('[data-collection-detail]')).toContainText(ids[3]);
  await expect(page.locator('[data-collection-detail]')).not.toContainText(ids[0]);
  await page.reload(); await expect(page.locator('[data-collection-detail]')).toContainText(ids[3]);
  await expect(page.locator('[data-collection-master] tbody tr')).toHaveCount(2);
  await page.locator('[data-collection-detail]').getByRole('link', { name: 'Inspect source entity', exact: true }).click();
  await expect(page.locator('[data-collection-detail]').getByRole('heading', { name: 'Entity 4', exact: true })).toBeVisible();
  await expect(page.locator('[data-collection-master] tbody tr')).toHaveCount(2);
  await page.getByRole('button', { name: 'Back to source', exact: true }).click();
  await expect(page.locator('[data-collection-detail]')).toContainText(ids[3]);
  await page.screenshot({ path: resolve(output, 'mono-warehouse-fixture.png'), fullPage: true });
  mark('Direct source collection; checkbox OR/AND; keyboard selection; source identity with duplicate names; Back and reload');

  await primary.getByRole('link', { name: 'Look up', exact: true }).click();
  await expect(page.locator('[data-collection-master]')).toContainText('Whale IS A mammal');
  await page.getByRole('textbox', { name: 'Search knowledge', exact: true }).fill('fixture');
  await page.getByRole('button', { name: 'Search', exact: true }).click();
  await expect(page.locator('[data-collection-master] tbody tr')).toHaveCount(3);
  // Read failures are summarized; the per-read detail sits behind a disclosure.
  await page.getByText('Some results could not be read', { exact: false }).click();
  await expect(page.getByText('Translation fixture unavailable', { exact: false }).first()).toBeVisible();
  assert.deepEqual([...new Set(queryReads.map(read => read.shape))].sort(), ['band_facts', 'define', 'describe', 'translate']);
  await page.locator('[data-collection-master]').getByRole('link', { name: 'Fixture combined answer.', exact: true }).click();
  assert.match(new URL(page.url()).searchParams.get('answer'), /^answer:[a-f0-9]{64}$/);
  assert.ok(!page.url().includes('Fixture+combined+answer'));
  await expect(page.locator('[data-collection-detail]')).toContainText('Conservative standing 1200');
  await expect(page.locator('[data-collection-detail]')).toContainText('Conservative standing 1300');
  const metadataBefore = previewReads, detailBefore = entityReads;
  await page.getByRole('button', { name: 'Inspect related subject' }).click();
  mark('Knowledge opens with claims; applicable methods combine; duplicate answers preserve measurements; failures preserve results');
  await page.locator('[data-collection-master]').getByRole('link', { name: 'Entity 1', exact: true }).click();
  const detail = page.locator('[data-collection-detail]');
  await expect(detail.getByRole('heading', { name: 'Entity 1', exact: true })).toBeVisible();
  // The selected result is shown through the standard lenses: one row, Means first, with sources.
  await expect(detail.getByRole('tablist', { name: 'Views of this subject' })).toHaveCount(1);
  await expect(detail.getByRole('tab', { name: 'Means', exact: true })).toHaveAttribute('aria-selected', 'true');
  await expect(detail.getByRole('heading', { name: /is a …/ })).toBeVisible();
  await detail.getByRole('button', { name: /1 source · 2 games/ }).click();
  await expect(detail.getByText('Fixture source')).toBeVisible();
  assert.equal(previewReads, metadataBefore, 'knowledge must not wait for evidence-count previews');
  assert.ok(entityReads > detailBefore, 'meanings and sources come from one inspection read');
  await expect(detail.getByText('Structure, standing and evidence', { exact: true })).toHaveCount(0);
  mark('Selected result opens through the standard lenses (one row, Means first, sources per strand); no hidden inspector');
  const reads = browseReads;
  // Related subjects open in the inspector drawer; the search and its selection stay put.
  await detail.getByRole('button', { name: 'Entity 2', exact: true }).click();
  await expect(page.getByRole('complementary', { name: /Inspector: Entity 2/ })).toBeVisible();
  assert.equal(browseReads, reads, 'opening a related subject must retain the search read');
  await expect(page.locator('[data-collection-master] tbody tr')).toHaveCount(3);
  await expect(detail.getByRole('heading', { name: 'Entity 1', exact: true })).toBeVisible();
  await page.reload();
  await expect(page.getByRole('complementary', { name: /Inspector: / })).toBeVisible();
  await expect(detail.getByRole('heading', { name: 'Entity 1', exact: true })).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(page.getByRole('complementary', { name: /Inspector: / })).toHaveCount(0);
  await page.screenshot({ path: resolve(output, 'mono-library-fixture.png'), fullPage: true });
  mark('Related subjects open in the inspector without losing the collection; selection and inspector survive reload');
  for (const width of [1440, 768, 390]) {
    await page.setViewportSize({ width, height: 1000 });
    assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), `overflow at ${width}`);
    await expect(primary.getByRole('link', { name: 'Look up', exact: true })).toHaveCount(1);
    const result = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa']).analyze();
    accessibility.push({ width, violations: result.violations.map(item => ({ id: item.id, nodes: item.nodes.map(node => node.target) })) });
  }
  await page.getByRole('button', { name: 'Back to results', exact: true }).click();
  await expect(page.locator('[data-collection-master]')).toBeVisible();
  await expect(page.getByRole('textbox', { name: 'Search knowledge', exact: true })).toHaveValue('fixture');
  await page.screenshot({ path: resolve(output, 'mono-mobile-fixture.png'), fullPage: true });
  mark('Desktop/tablet/mobile navigation and overflow; mobile return retains query and collection');
  await page.setViewportSize({ width: 1440, height: 1000 });
  await page.goto(base + 'explore/sources');
  failCatalog = true;
  await page.getByRole('button', { name: 'Refresh catalogue', exact: true }).click();
  await expect(page.getByText('Catalogue fixture unavailable', { exact: false }).first()).toBeVisible();
  failCatalog = false;
  await page.getByRole('button', { name: 'Refresh catalogue', exact: true }).click();
  await expect(page.locator('[data-collection-master] tbody tr')).toHaveCount(4);
  mark('Catalogue failures remain distinguishable from empty results and can be retried');
  await page.goto(base + 'explore/source/Source%20A');
  await expect(page.getByText('Several source records have this name.', { exact: false })).toBeVisible();
  await page.getByRole('link', { name: `Source A · ${ids[3]}`, exact: true }).click();
  await expect(page.getByText(ids[3], { exact: true }).first()).toBeVisible();
  mark('Ambiguous source-name bookmarks require a canonical source choice');
  await page.goto(base + 'query?q=fixture&other=second&relation=IS_A&lang=fr');
  await expect(page.locator('[data-collection-master] tbody tr')).toHaveCount(3);
  await expect.poll(() => [...new Set(queryReads.filter(read => read.topic2 === 'second').map(read => read.shape))]).toContain('path');
  assert.ok(queryReads.some(read => read.shape === 'related' && read.relation_type === 'IS_A'));
  assert.ok(queryReads.some(read => read.shape === 'translate' && read.lang === 'fr'));
  assert.ok(!queryReads.some(read => read.shape === 'generate'));
  await page.getByRole('group', { name: 'Type', exact: true }).getByRole('checkbox', { name: 'answer', exact: false }).click();
  await expect(page.locator('[data-collection-master] tbody tr')).toHaveCount(1);
  await page.locator('[data-collection-master]').getByRole('link', { name: 'Fixture combined answer.', exact: true }).click();
  await page.reload();
  await expect(page.locator('[data-collection-master] tbody tr')).toHaveCount(1);
  await expect(page.locator('[data-collection-detail]')).toContainText('Fixture combined answer.');
  mark('Second-subject, relation and language criteria compose; result filters and selected answers survive reload; generation remains a calculation');
  const scopeAddress = 'explore?q=fixture&relation=IS_A&method=entities&method=beam&method=band_facts&depth=7&breadth=9&limit=83&band=1&epoch=2026&world=language&rank=confidence';
  await page.goto(base + scopeAddress);
  await expect.poll(() => queryReads.some(read => read.shape === 'beam' && read.depth === 7 && read.breadth === 9 && read.limit === 83 && read.bands?.[0] === 1)).toBe(true);
  await page.getByText('Search criteria', { exact: true }).click();
  await expect(page.getByRole('checkbox', { name: 'identity · 12 consensus rows', exact: true })).toBeChecked();
  await page.getByRole('checkbox', { name: 'relation · 8 consensus rows', exact: true }).check();
  await page.getByRole('button', { name: 'Apply search criteria', exact: true }).click();
  await expect.poll(() => queryReads.some(read => read.shape === 'band_facts' && read.limit === 83 && read.bands?.join(',') === '1,2')).toBe(true);
  await page.reload();
  assert.equal(new URL(page.url()).searchParams.get('epoch'), '2026');
  assert.equal(new URL(page.url()).searchParams.get('world'), 'language');
  await page.locator('[data-collection-master]').getByRole('link', { name: 'Entity 1', exact: true }).click();
  const presentations = page.locator('[data-collection-detail]').getByRole('tablist', { name: 'Views of this subject' });
  await presentations.getByRole('tab', { name: 'Classification', exact: true }).click();
  await page.getByRole('link', { name: 'Broader fixture class', exact: true }).click();
  assert.equal(new URL(page.url()).searchParams.get('selected'), ids[1]);
  assert.equal(new URL(page.url()).searchParams.get('presentation'), 'classification');
  assert.equal(new URL(page.url()).searchParams.get('rank'), 'confidence');
  await expect(page.locator('[data-collection-master] tbody tr')).toHaveCount(3);
  for (const label of ['Web', 'Placement', 'Made of', 'Appears in']) await expect(presentations.getByRole('tab', { name: label, exact: true })).toBeVisible();
  await expect(page.locator('[data-collection-detail]').getByRole('link', { name: 'Open as a page', exact: true })).toBeVisible();
  await presentations.getByRole('tab', { name: 'Made of', exact: true }).click();
  assert.equal(new URL(page.url()).searchParams.get('presentation'), 'made-of');
  await page.reload();
  await expect(presentations.getByRole('tab', { name: 'Made of', exact: true })).toHaveAttribute('aria-selected', 'true');
  mark('Band and execution controls reach native query bodies; peer coordinates, classification pivots and the selected lens survive reload');

  await page.goto(base + scopeAddress + `&selected=${ids[0]}&presentation=comparison&other=second`);
  await expect(page.getByRole('textbox', { name: 'first entity', exact: true })).toHaveValue(ids[0]);
  await page.getByRole('textbox', { name: 'second entity', exact: true }).fill('another subject');
  await page.getByRole('button', { name: 'Compare', exact: true }).click();
  await expect.poll(() => comparisonReads.some(([x, y]) => x === ids[0] && y === 'another subject')).toBe(true);
  await page.getByRole('link', { name: 'Comparison entity B', exact: true }).click();
  assert.equal(new URL(page.url()).searchParams.get('selected'), ids[1]);
  assert.equal(new URL(page.url()).searchParams.get('other'), 'another subject');
  assert.equal(new URL(page.url()).searchParams.get('rank'), 'confidence');
  mark('Comparison reads use the selected subject and shared second-subject context; resulting subject pivots retain that comparison');

  await page.goto(base + 'explore/mesh?epoch=2026&world=language&rank=confidence');
  const meshObservation = '  fixture relation  ';
  await page.getByRole('textbox', { name: 'Choose an entity, then inspect its relations', exact: true }).fill(meshObservation);
  await page.getByRole('button', { name: 'Find mesh entry', exact: true }).click();
  assert.equal(new URL(page.url()).searchParams.get('q'), meshObservation);
  assert.equal(new URL(page.url()).searchParams.get('presentation'), 'mesh');
  await page.locator('[data-collection-master]').getByRole('link', { name: 'Entity 1', exact: true }).click();
  await expect(page.getByRole('button', { name: 'IS A Fixture related parent', exact: false })).toBeVisible();
  await page.getByRole('button', { name: 'IS A Fixture related parent', exact: false }).click();
  assert.equal(new URL(page.url()).searchParams.get('selected'), ids[1]);
  await expect(page.locator(`[data-entity-ref="${ids[2]}"]`)).toContainText(ids[2]);
  await page.locator(`[data-entity-ref="${ids[2]}"]`).click();
  assert.equal(new URL(page.url()).searchParams.get('selected'), ids[2]);
  assert.equal(new URL(page.url()).searchParams.get('q'), meshObservation);
  assert.equal(new URL(page.url()).searchParams.get('epoch'), '2026');
  assert.equal(new URL(page.url()).searchParams.get('world'), 'language');
  assert.equal(new URL(page.url()).searchParams.get('rank'), 'confidence');
  await expect(page.locator('[data-collection-master] tbody tr')).toHaveCount(3);
  await page.reload();
  await expect(page.locator('[data-collection-detail]')).toContainText(ids[2]);
  for (const width of [1440, 390]) {
    await page.setViewportSize({ width, height: 1000 });
    assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), `relation presentation overflow at ${width}`);
    const result = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa']).analyze();
    accessibility.push({ view: 'embedded-relations', width, violations: result.violations.map(item => ({ id: item.id, nodes: item.nodes.map(node => node.target) })) });
  }
  await page.screenshot({ path: resolve(output, 'mono-world-relations-fixture.png'), fullPage: true });
  await page.setViewportSize({ width: 1440, height: 1000 });
  await page.getByRole('button', { name: 'Compare →', exact: true }).click();
  assert.equal(new URL(page.url()).searchParams.get('presentation'), 'comparison');
  assert.equal(new URL(page.url()).searchParams.get('q'), meshObservation);
  await expect(page.getByRole('textbox', { name: 'first entity', exact: true })).toHaveValue(ids[2]);
  mark('Relation search opens the actual shared presentation; unlabelled subjects remain navigable; parent/member pivots, comparison and reload preserve the originating collection and scope');
  const headerObservation = '  header observation  ';
  // The header search is the command palette (Ctrl+K); "Search for …" opens full results.
  await page.getByRole('button', { name: 'Search (Ctrl+K)', exact: true }).click();
  await page.getByRole('combobox').fill(headerObservation);
  await page.getByRole('option', { name: /Search for “header observation”/ }).click();
  assert.equal(new URL(page.url()).searchParams.get('q'), headerObservation);
  assert.equal(new URL(page.url()).searchParams.get('epoch'), '2026');
  assert.equal(new URL(page.url()).searchParams.get('world'), 'language');
  assert.equal(new URL(page.url()).searchParams.get('selected'), null);
  assert.equal(new URL(page.url()).searchParams.get('presentation'), null);
  mark('Header search preserves the full observation and peer criteria while clearing the previous selection');

  await page.goto(base + scopeAddress + `&selected=${gameId}&presentation=chess-game`);
  await expect(page.getByRole('heading', { name: 'Headers', exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Next move', exact: true }).click();
  const board = page.getByRole('table', { name: 'Chess position. Black to move.', exact: true });
  await expect(board.getByRole('row')).toHaveCount(8);
  await expect(board.getByRole('cell')).toHaveCount(64);
  await expect(board.getByRole('cell', { name: 'e4: White pawn', exact: true })).toBeVisible();
  for (const width of [1440, 390]) {
    await page.setViewportSize({ width, height: 1000 });
    assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), `game overflow at ${width}`);
    const title = page.locator('[data-collection-detail] header h2').first();
    // The pane titles itself with the recorded label when one is known, else the canonical reference.
    await expect(title).toHaveText(/^(Fixture entity detail|[a-f0-9]{32})$/);
    assert.ok(await title.evaluate(element => element.scrollWidth <= element.clientWidth + 1), 'The title must wrap within the detail pane');
    const result = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa']).analyze();
    accessibility.push({ view: 'embedded-game', width, violations: result.violations.map(item => ({ id: item.id, nodes: item.nodes.map(node => node.target) })) });
  }
  await page.setViewportSize({ width: 1440, height: 1000 });
  await page.getByRole('link', { name: 'open this position ›', exact: true }).click();
  assert.equal(new URL(page.url()).searchParams.get('selected'), positionId);
  assert.equal(new URL(page.url()).searchParams.get('presentation'), 'knowledge');
  assert.equal(new URL(page.url()).searchParams.get('epoch'), '2026');
  await page.goBack();
  // A player is a name, not an entity of this collection: the link opens the name's own page.
  assert.equal(await page.getByRole('link', { name: 'Fixture player', exact: true }).getAttribute('href'), '/chess/players/Fixture%20player');
  await expect(page.locator('[data-collection-master] tbody tr')).toHaveCount(3);
  await page.screenshot({ path: resolve(output, 'mono-world-game-fixture.png'), fullPage: true });
  mark('Game headers and board-position pivots retain the originating collection and scope; players link by name');
  await page.goto(base + 'explore/notfound/calculated-fixture?epoch=2026');
  await expect(page.getByText('calculated identity · not reported present', { exact: true })).toBeVisible();
  await expect(page.getByText('A calculated address does not establish admission', { exact: false })).toBeVisible();
  await page.getByRole('link', { name: 'Search known content', exact: true }).click();
  assert.equal(new URL(page.url()).searchParams.get('q'), 'calculated-fixture');
  assert.equal(new URL(page.url()).searchParams.get('epoch'), '2026');
  const observation = '  exact observation  ';
  await page.goto(base + `explore/resolve/${encodeURIComponent(observation)}?epoch=2026`);
  await expect(page).toHaveURL(/\/explore\?/);
  assert.equal(new URL(page.url()).searchParams.get('q'), observation);
  assert.equal(new URL(page.url()).searchParams.get('epoch'), '2026');
  assert.equal(resolveReads, 0, 'Legacy search entry must not turn a calculated identity into a resolved subject');
  await page.goto(base + `explore/resolve/${unresolvedId}?epoch=2026`);
  await expect(page.getByText('This address is not recorded as present.', { exact: false })).toBeVisible();
  assert.equal(new URL(page.url()).searchParams.get('selected'), unresolvedId);
  assert.equal(new URL(page.url()).searchParams.get('epoch'), '2026');
  mark('Calculated geometry is explicit; legacy search preserves the whole observation and scope; an unreported direct address is retained without an admission claim');
  assert.deepEqual(errors, []);
  assert.deepEqual(accessibility.flatMap(item => item.violations), []);
  passed = true;
} finally {
  await writeFile(resolve(output, 'mono-receipt.json'), JSON.stringify({ status: passed ? 'passed' : 'failed', kind: 'isolated-browser-fixtures', checks, errors, accessibility }, null, 2));
  await browser?.close();
  await server.close();
}
