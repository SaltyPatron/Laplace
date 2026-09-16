// Real DataView/UploadProvider/ResultWorkspace interactions, with HTTP intercepted.
// No fixture request is allowed to reach an installed substrate.
import assert from 'node:assert/strict';
import { createServer } from 'vite';
import { chromium, expect } from '@playwright/test';
import { mkdtemp, readFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
const artifacts = await mkdtemp(join(tmpdir(), 'laplace-data-ui-'));
const server = await createServer({ server: { host: '127.0.0.1', port: 0, strictPort: true }, plugins: [{ name: 'data-fixture', configureServer(server) {
  server.middlewares.use(async (req, res, next) => {
    if (req.url?.split('?')[0] !== '/__data_test') return next();
    try { const html = await server.transformIndexHtml(req.url, '<!doctype html><html lang="en"><head><meta charset="UTF-8"><title>Data workspace fixture</title></head><body><div id="root"></div><script type="module" src="/tests/data-fixture.tsx"></script></body></html>'); res.statusCode = 200; res.setHeader('Content-Type', 'text/html'); res.end(html); } catch (error) { next(error); }
  });
} }] });
let browser, context, passed = false;
try {
  await server.listen(); browser = await chromium.launch(); context = await browser.newContext({ acceptDownloads: true });
  await context.tracing.start({ screenshots: true, snapshots: true, sources: true });
  const page = await context.newPage(); const errors = []; page.on('pageerror', (error) => errors.push(error.message));
  const original = Buffer.from('\ufeff King != king\r\ne\u0301\n'); const calls = []; let held;
  const receipt = { file_id: 'a'.repeat(32), document_id: 'b'.repeat(32), content_id: 'c'.repeat(32), metadata_id: 'd'.repeat(32), source_id: 'e'.repeat(32), source: 'fixture', bytes: original.length, modality: null };
  await page.route('**/v1/**', async (route) => {
    const path = new URL(route.request().url()).pathname;
    if (path === '/v1/content/text') { calls.push(route.request().postDataJSON()); held = route; return; }
    if (path === `/v1/content/${receipt.file_id}`) return route.fulfill({ json: { ...receipt, kind: 'file', requested_id: receipt.file_id, name: 'exact.txt', path: 'exact.txt', text: original.toString('utf8'), content_base64: original.toString('base64'), contexts: [], modified_at: null } });
    return route.fulfill({ status: 501, json: { error: { message: `Unprovided fixture route ${path}` } } });
  });
  await page.goto(`${server.resolvedUrls.local[0]}__data_test`);
  await page.getByLabel('Files', { exact: true }).setInputFiles([{ name: 'exact.txt', mimeType: 'text/plain', buffer: original }, { name: 'later.txt', mimeType: 'text/plain', buffer: Buffer.from('later') }]);
  await expect(page.getByRole('button', { name: 'Ingest 2 queued artifacts', exact: true })).toBeEnabled(); assert.equal(calls.length, 0);
  const pathInput = page.getByRole('textbox', { name: 'Relative path for exact.txt', exact: true });
  await pathInput.focus(); await pathInput.press('End'); await pathInput.press('a'); await expect(pathInput).toBeFocused();
  await pathInput.press('Backspace'); await expect(pathInput).toHaveValue('exact.txt');
  await page.getByRole('button', { name: 'Ingest 2 queued artifacts', exact: true }).click();
  await expect.poll(() => calls.length).toBe(1); assert.equal(calls[0].content_base64, original.toString('base64'));
  await page.getByRole('button', { name: 'Another workspace', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Another workspace', exact: true })).toBeVisible();
  await held.fulfill({ json: receipt });
  await page.getByRole('button', { name: 'Open data workspace', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Ingest 1 queued artifact', exact: true })).toBeEnabled(); assert.equal(calls.length, 1);
  await page.getByRole('button', { name: 'Read admitted bytes', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Download returned bytes', exact: true })).toBeEnabled();
  const downloadPromise = page.waitForEvent('download'); await page.getByRole('button', { name: 'Download returned bytes', exact: true }).click();
  const download = await downloadPromise; assert.deepEqual(await readFile(await download.path()), original);
  await page.getByRole('button', { name: 'Switch fixture tenant', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Ingest 0 queued artifacts', exact: true })).toBeDisabled();
  await expect(page.getByRole('button', { name: 'Read admitted bytes', exact: true })).toHaveCount(0);
  await page.getByRole('button', { name: 'Response workspace', exact: true }).click();
  await page.getByRole('checkbox', { name: 'Select Record 1', exact: true }).check();
  await page.getByRole('button', { name: 'Next rows', exact: true }).click();
  await page.getByRole('checkbox', { name: 'Select Record 26', exact: true }).check();
  await page.getByRole('button', { name: 'Inspect Record 26', exact: true }).click();
  await expect(page.getByRole('region', { name: 'Received row detail', exact: true })).toContainText('9223372036854775807');
  await page.getByRole('button', { name: 'Replace response', exact: true }).click();
  await expect(page.getByRole('region', { name: 'Working selection', exact: true }).or(page.getByLabel('Working selection', { exact: true }))).toContainText('2 retained from earlier responses');
  const exportPromise = page.waitForEvent('download'); await page.getByRole('button', { name: 'Export selected rows', exact: true }).click();
  const exported = JSON.parse(await readFile(await (await exportPromise).path(), 'utf8'));
  assert.deepEqual(exported.rows.map((item) => item.response_ordinal), [1, 26]); assert.equal(exported.rows[0].value.wide, '9223372036854775807');
  for (const width of [360, 768, 1280]) { await page.setViewportSize({ width, height: 900 });
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), true, `page overflow at ${width}`); }
  assert.deepEqual(errors, []); passed = true;
  console.log('DATA_UI_OK explicit selection; exact byte admission/readback; route-stop and retained receipts; tenant isolation; plural response selection and exact received-value export; narrow layout');
} finally {
  if (context) await context.tracing.stop({ path: join(artifacts, 'trace.zip') });
  if (browser) await browser.close(); await server.close();
  if (passed) await rm(artifacts, { recursive: true, force: true }); else console.error(`Data UI diagnostics: ${artifacts}`);
}
