// Real installed-browser reads. The caller issues and revokes the verification key.
import { chromium, expect as baseExpect } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { mkdir, writeFile } from 'node:fs/promises';
import { resolve } from 'node:path';
const expect = baseExpect.configure({ timeout: 60000 });
const base = 'https://dev.hartonomous.com';
const key = process.env.LAPLACE_API_KEY;
if (!key?.startsWith('sk-laplace-')) throw new Error('An enrolled verification key is required.');
const output = resolve(process.env.LAPLACE_LIBRARY_UI_EVIDENCE || '../../artifacts/library-ui/live-mono');
await mkdir(output, { recursive: true });
const browser = await chromium.launch({ headless: true });
const report = { kind: 'installed-browser-live-reads', status: 'failed', origin: base, checks: [], accessibility: [], errors: [], detail_requests: [] };
let page;
const mark = name => { report.checks.push(name); console.log('PASS ' + name); };
try {
  const context = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
  await context.route('**/*', route => {
    const url = new URL(route.request().url());
    return url.origin === base && url.pathname.startsWith('/v1/')
      ? route.continue({ headers: { ...route.request().headers(), authorization: `Bearer ${key}` } })
      : route.continue();
  });
  page = await context.newPage(); page.setDefaultTimeout(60000);
  page.on('request', request => {
    const url = new URL(request.url());
    if (/^\/v1\/explore\/entities\/[a-f0-9]{32}(\/preview)?$/.test(url.pathname)) report.detail_requests.push(url.pathname);
  });
  report.search_reads = [];
  page.on('response', async response => {
    if (new URL(response.url()).pathname !== '/v1/query' || response.request().method() !== 'POST') return;
    const body = response.request().postDataJSON();
    const data = await response.json().catch(() => ({}));
    report.search_reads.push({ method: body.shape, status: response.status(), rows: data.rows?.length, error: data.error?.message });
  });
  page.on('pageerror', error => report.errors.push(error.message.replaceAll(key, '[credential]')));
  await page.goto(base + '/');
  await expect(page.getByRole('heading', { name: 'Knowledge', exact: true })).toBeVisible();
  await expect(page.getByRole('navigation', { name: 'Primary navigation' }).getByRole('link', { name: 'Sources', exact: true })).toHaveCount(0);
  const sections = page.getByRole('navigation', { name: 'Knowledge sections', exact: true });
  for (const label of ['Search', 'Sources', 'Highway', 'Constellation', 'Unicode Glome', 'Storage Proof', 'Forward Trace', 'Mesh', 'Matchup', 'Audit', 'Architecture']) {
    await expect(sections.getByRole('link', { name: label, exact: true })).toBeVisible();
  }
  mark('Knowledge is the opening workspace; provenance is not primary navigation');
  const catalogueRead = page.waitForResponse(response => new URL(response.url()).pathname === '/v1/explore/catalog');
  await sections.getByRole('link', { name: 'Sources', exact: true }).click();
  const response = await catalogueRead;
  if (response.status() !== 200) throw new Error('Installed source catalogue returned HTTP ' + response.status());
  const catalogue = await response.json();
  report.sources = catalogue.sources.map(source => ({ key: source.key, stage: source.stage, layer: source.layer, id_hex: source.id_hex }));
  if (!catalogue.sources.length) throw new Error('Installed source catalogue is empty.');
  await expect(page.locator('[data-collection-master] tbody tr')).toHaveCount(catalogue.sources.length);
  const primary = page.getByRole('navigation', { name: 'Primary navigation' });
  await expect(primary.getByRole('link', { name: 'Knowledge', exact: true })).toBeVisible();
  await expect(primary.getByRole('link', { name: 'Sources', exact: true })).toHaveCount(0);
  const stage = page.getByRole('group', { name: 'Stage', exact: true }).getByRole('checkbox').first();
  await stage.click(); await expect(stage).toBeChecked();
  const params = new URL(page.url()).searchParams;
  const filtered = catalogue.sources.filter(source => params.getAll('stage').includes(source.stage ?? ''));
  await expect(page.locator('[data-collection-master] tbody tr')).toHaveCount(filtered.length);
  const selected = page.locator('[data-collection-master] a').first();
  const sourceName = await selected.innerText();
  await selected.click();
  await expect(page.locator('[data-collection-detail]')).toContainText(sourceName);
  await expect(page.locator('[data-collection-master] tbody tr')).toHaveCount(filtered.length);
  const address = page.url();
  await page.reload(); await expect(page.locator('[data-collection-detail]')).toContainText(sourceName);
  if (page.url() !== address) throw new Error('Source reload changed its address.');
  await page.screenshot({ path: resolve(output, 'warehouse.png'), fullPage: true });
  mark('Installed source catalogue, direct navigation, checkbox filter and source master/detail survive reload');
  await primary.getByRole('link', { name: 'Knowledge', exact: true }).click();
  await page.getByRole('textbox', { name: 'Search knowledge', exact: true }).fill('dog');
  const searchRead = page.waitForResponse(response => new URL(response.url()).pathname === '/v1/explore/browse');
  await page.getByRole('button', { name: 'Search', exact: true }).click();
  const searchResponse = await searchRead;
  if (searchResponse.status() !== 200) throw new Error('Installed library returned HTTP ' + searchResponse.status());
  const search = await searchResponse.json();
  if (!search.hits.length) throw new Error('The live dog search returned no entities.');
  await expect.poll(() => page.locator('[data-collection-master] tbody tr').count()).toBeGreaterThanOrEqual(search.hits.length);
  await expect(page.getByRole('button', { name: 'Refresh search', exact: true })).toBeEnabled();
  const first = search.hits[0];
  const meaningRead = page.waitForResponse(response => {
    const url = new URL(response.url());
    return url.pathname === '/v1/explore/lexicon' && url.searchParams.get('x') === first.id_hex;
  });
  const selectionStarted = Date.now();
  await page.locator('[data-collection-master]').getByRole('link', { name: first.label, exact: true }).first().click();
  await expect(page.locator('[data-collection-detail]').getByRole('heading', { name: first.label, exact: true })).toBeVisible();
  report.selected_heading_ms = Date.now() - selectionStarted;
  if (report.detail_requests.some(path => path.includes(first.id_hex))) throw new Error('Opening known content unexpectedly requested structural/evidence inspection.');
  const meaningsResponse = await meaningRead;
  if (meaningsResponse.status() !== 200) throw new Error('Selected meanings returned HTTP ' + meaningsResponse.status());
  const meanings = await meaningsResponse.json();
  report.selected_knowledge = { readings: meanings.readings?.length ?? 0, connections: meanings.connections?.length ?? 0 };
  if (meanings.readings?.length) await expect(page.locator('[data-collection-detail]')).toContainText(meanings.readings[0].text);
  await expect.poll(() => page.locator('[data-collection-master] tbody tr').count()).toBeGreaterThanOrEqual(search.hits.length);
  await expect(page.locator('[data-collection-detail]')).toContainText('Meanings and usage');
  await page.screenshot({ path: resolve(output, 'library.png'), fullPage: true });
  mark('Installed native search and entity detail keep the received results visible');
  if (report.search_reads.some(read => read.status !== 200 || read.method === 'generate')) throw new Error('The combined search included a failed read or an unsolicited calculation.');
  for (const width of [1440, 768, 390]) {
    await page.setViewportSize({ width, height: 1000 });
    if (!await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1)) throw new Error('Horizontal page overflow at ' + width);
    await expect(primary.getByRole('link', { name: 'Knowledge', exact: true })).toBeVisible();
  await expect(primary.getByRole('link', { name: 'Sources', exact: true })).toHaveCount(0);
    const scan = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa']).analyze();
    report.accessibility.push({ width, violations: scan.violations.map(item => ({ id: item.id, nodes: item.nodes.map(node => node.target) })) });
    if (scan.violations.length) throw new Error('Installed accessibility violations at ' + width);
  }
  await page.getByRole('button', { name: 'Back to results', exact: true }).click();
  await expect(page.locator('[data-collection-master]')).toBeVisible();
  await expect(page.getByRole('textbox', { name: 'Search knowledge', exact: true })).toHaveValue('dog');
  await page.screenshot({ path: resolve(output, 'mobile.png'), fullPage: true });
  mark('Installed desktop/tablet/mobile navigation and mobile return retain the native query');
  if (report.errors.length) throw new Error('Installed browser exceptions were recorded.');
  report.status = 'passed';
} catch (error) {
  report.failure = String(error.stack || error).replaceAll(key, '[credential]');
  if (page) {
    report.failed_address = page.url();
    report.failed_rows = await page.locator('[data-collection-master] tbody tr').allTextContents();
    await page.screenshot({ path: resolve(output, 'failure.png'), fullPage: true });
  }
  throw error;
}
finally {
  await writeFile(resolve(output, 'receipt.json'), JSON.stringify(report, null, 2));
  await browser.close();
}
