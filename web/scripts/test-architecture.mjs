import assert from 'node:assert/strict';
import { createServer, preview } from 'vite';
import { chromium, expect } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { tmpdir } from 'node:os';

const production = process.env.LAPLACE_ARCHITECTURE_PRODUCTION === '1';
const artifacts = join(process.env.LAPLACE_WORK ?? tmpdir(), 'operation-atlas', production ? 'product-production' : 'product-development');
await mkdir(artifacts, { recursive: true });
const server = production
  ? await preview({ preview: { host: '127.0.0.1', port: 0, strictPort: true } })
  : await createServer({ server: { host: '127.0.0.1', port: 0, strictPort: true } });
if (!production) await server.listen();
const base = server.resolvedUrls.local[0].replace(/\/$/, '');
let browser;
try {
  browser = await chromium.launch({ channel: process.env.PLAYWRIGHT_CHANNEL || undefined });
  const context = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
  await context.route('**/v1/**', (route) => route.fulfill({ json: route.request().url().includes('/auth/me') ? { authenticated: false, providers: [] } : {} }));
  await context.route('**/health**', (route) => route.fulfill({ json: { status: 'healthy' } }));
  const page = await context.newPage();
  const errors = []; page.on('pageerror', (e) => errors.push(e.message));
  const catalog = JSON.parse(await readFile('public/architecture/catalog.json', 'utf8'));
  assert.equal(catalog.coverage.operations, 283);
  assert.equal(catalog.coverage.registrations, 1059);
  const files = new Set();
  for (const d of catalog.diagrams) for (const a of d.assets) files.add(a);
  // Every diagram export must be served by the same deployed web application.
  for (const a of files) {
    const response = await context.request.get(`${base}/architecture/${a}`);
    assert.equal(response.status(), 200, a);
    assert.ok((await response.body()).length > 0, a);
    assert.ok(!response.headers()['content-type']?.includes('text/html'), `SPA fallback masked missing asset ${a}`);
  }
  await page.goto(`${base}/explore/architecture`);
  await expect(page.getByRole('heading', { name: 'Architecture & operations', exact: true })).toBeVisible();
  await expect(page.getByRole('link', { name: 'Architecture & operations', exact: true })).toHaveAttribute('aria-current', 'page');
  const diagram = page.getByRole('img', { name: 'Laplace — construction prerequisites and recurring operation lanes' });
  await expect(diagram).toBeVisible();
  await expect.poll(() => diagram.evaluate((img) => img.complete && img.naturalWidth > 0)).toBe(true);
  await page.screenshot({ path: join(artifacts, 'desktop.png'), fullPage: true });
  await page.getByLabel('Diagram scale').selectOption('100');
  assert.equal(await diagram.evaluate((img) => Math.round(img.getBoundingClientRect().width)), await diagram.evaluate((img) => img.naturalWidth));
  await page.getByRole('button', { name: 'Expand Laplace — construction prerequisites and recurring operation lanes', exact: true }).click();
  await expect(page.getByRole('dialog')).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(page.getByRole('dialog')).toHaveCount(0);
  await page.getByLabel('Evidence kind').selectOption('Source trace');
  await page.getByLabel('Find a diagram').fill('circuit decomposition');
  await expect(page.getByRole('status').filter({ hasText: 'matching diagrams' })).toHaveText('1 matching diagrams and figures');
  await page.getByLabel('Diagram', { exact: true }).selectOption('trace-diagrams/monorepo-model-circuits');
  await expect(page).toHaveURL(/diagram=trace-diagrams%2Fmonorepo-model-circuits/);
  await page.reload();
  await expect(page.getByRole('heading', { name: 'Monorepo — circuit decomposition and snapshot lifetime' })).toBeVisible();
  const download = page.waitForEvent('download');
  await page.getByRole('link', { name: 'PUML', exact: true }).click();
  assert.equal((await download).suggestedFilename(), 'monorepo-model-circuits.puml');
  await page.getByRole('link', { name: '24.6', exact: true }).click();
  await expect(page).toHaveURL(/operation=24.6/);
  await expect(page.locator('summary').filter({ hasText: '24.6 Derive each circuit' })).toBeVisible();
  await expect(page.getByText('Inputs', { exact: true })).toBeVisible();
  await expect(page.getByRole('link', { name: 'Monorepo — circuit decomposition and snapshot lifetime' })).toBeVisible();
  await page.getByRole('link', { name: 'Show all operations' }).click();
  await expect(page.getByRole('status').filter({ hasText: 'operation contracts' })).toHaveText('283 operation contracts');
  await page.goto(`${base}/explore/architecture?section=registrations`);
  await expect(page.getByRole('status').filter({ hasText: 'source registrations' })).toHaveText('1059 source registrations');
  await page.getByRole('button', { name: 'Next registrations' }).click();
  await expect(page.getByText('Page 2 of 27')).toBeVisible();
  await page.goto(`${base}/explore/architecture?section=conflicts&q=P1.`);
  await expect(page.locator('h3').filter({ hasText: 'Implementation observation' }).first()).toBeVisible();
  await page.goto(`${base}/explore/architecture?section=sources&q=cmd_turn`);
  await expect(page.getByRole('link', { name: 'cmd_turn', exact: true })).toBeVisible();
  await page.getByRole('link', { name: 'cmd_turn', exact: true }).click();
  await expect(page.locator('pre').filter({ hasText: 'cmd_turn' })).toBeVisible();
  const sourceMap = JSON.parse(await readFile('public/architecture/source-map.json', 'utf8'));
  await page.goto(`${base}/explore/architecture?section=sources&file=${sourceMap.symbols[0].file}&expected=incorrect`);
  await expect(page.getByText(/syntax snapshot differs from the source hash/)).toBeVisible();
  await page.goto(`${base}/explore/architecture?diagram=absent`);
  await expect(page.getByRole('heading', { name: 'Diagram not found' })).toBeVisible();
  await page.getByRole('link', { name: 'Open whole lifecycle' }).click();
  await expect(page.getByRole('img')).toBeVisible();
  await page.setViewportSize({ width: 390, height: 844 });
  await expect(page.locator('details').filter({ has: page.locator('summary', { hasText: 'Explore tools' }) })).not.toHaveAttribute('open');
  await page.getByLabel('Diagram scale').selectOption('100');
  await diagram.scrollIntoViewIfNeeded();
  await page.screenshot({ path: join(artifacts, 'mobile.png'), fullPage: true });
  assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), 'Mobile document must not overflow horizontally');
  const accessibility = await new AxeBuilder({ page }).include('#main-content').analyze();
  assert.deepEqual(accessibility.violations.map((v) => ({ id: v.id, nodes: v.nodes.map((n) => n.target) })), []);
  // A fresh context avoids the shared read cache and verifies recoverable packaging failure.
  const failure = await browser.newContext();
  await failure.route('**/v1/**', (route) => route.fulfill({ json: { authenticated: false, providers: [] } }));
  await failure.route('**/health**', (route) => route.fulfill({ json: { status: 'healthy' } }));
  let fail = true;
  await failure.route('**/architecture/catalog.json', (route) => fail ? route.fulfill({ status: 503, body: 'Unavailable' }) : route.continue());
  const failedPage = await failure.newPage();
  await failedPage.goto(`${base}/explore/architecture`);
  await expect(failedPage.getByRole('alert')).toContainText('503');
  fail = false;
  await failedPage.getByRole('button', { name: 'Retry architecture' }).click();
  await expect(failedPage.getByRole('heading', { name: 'Architecture & operations', exact: true })).toBeVisible();
  assert.deepEqual(errors, []);
  const report = { production, exportsServed: files.size, diagrams: catalog.diagrams.length,
    operations: catalog.coverage.operations, registrations: catalog.coverage.registrations,
    checks: ['application route', 'existing navigation', 'original-size rendering', 'panel expansion', 'filtering', 'deep-link reload', 'UML download', 'operation comparisons', 'registration pagination', 'conflicts', 'source body', 'snapshot mismatch', 'missing diagram', 'mobile overflow', 'axe accessibility', 'resource retry'], errors };
  await writeFile(join(artifacts, 'checks.json'), JSON.stringify(report, null, 2));
  console.log(JSON.stringify({ ...report, artifacts }));
} finally {
  await browser?.close();
  if (production) await new Promise((resolve) => server.httpServer.close(resolve));
  else await server.close();
}
