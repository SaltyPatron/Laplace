// Exercises shipped React components and routed views in Chromium. Fixture HTTP
// responses isolate presentation/transport behavior; they are not substrate proof.
import assert from 'node:assert/strict';
import { createServer } from 'vite';
import { chromium, expect } from '@playwright/test';
import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
const artifacts = await mkdtemp(join(tmpdir(), 'laplace-workspace-ui-'));
const server = await createServer({
  server: { host: '127.0.0.1', port: 0, strictPort: true },
  plugins: [{ name: 'workspace-test-document', configureServer(server) {
    server.middlewares.use(async (req, res, next) => {
      if (req.url?.split('?')[0] !== '/__workspace_test') return next();
      try {
        const html = await server.transformIndexHtml(req.url, '<!doctype html><html lang="en"><head><meta charset="UTF-8"><title>Workspace interaction fixture</title></head><body><div id="root"></div><script type="module" src="/tests/workspace-fixture.tsx"></script></body></html>');
        res.statusCode = 200; res.setHeader('Content-Type', 'text/html'); res.end(html);
      } catch (error) { next(error); }
    });
  } }],
});
let browser, context;
let passed = false;
try {
  await server.listen();
  browser = await chromium.launch();
  context = await browser.newContext({ viewport: { width: 1280, height: 900 } });
  await context.tracing.start({ screenshots: true, snapshots: true, sources: true });
  const page = await context.newPage();
  const errors = []; page.on('pageerror', (error) => errors.push(error.message));
  let delayed = [], readCalls = 0;
  await page.route('**/__fixture/**', async (route) => {
    if (route.request().url().includes('/independent')) return route.fulfill({ json: { value: 'Independent pane ready' } });
    readCalls++;
    delayed.push(route);
  });
  const base = server.resolvedUrls.local[0].replace(/\/$/, '');
  await page.goto(`${base}/__workspace_test`);
  await expect(page.getByTestId('independent')).toHaveText('Independent pane ready');
  await expect(page.getByTestId('read')).toHaveText('');
  await expect.poll(() => readCalls).toBeGreaterThan(0);
  const before = readCalls;
  await page.getByRole('button', { name: 'Refresh fixture', exact: true }).click();
  await page.waitForTimeout(350); // More than three poll intervals, provider still held.
  assert.equal(readCalls, before, 'slow reads must not accumulate polling requests');
  await page.getByRole('button', { name: 'Change scope', exact: true }).click();
  await expect.poll(() => delayed.some((r) => r.request().url().endsWith('scope=B'))).toBe(true);
  const current = delayed.find((r) => r.request().url().endsWith('scope=B'));
  await current.fulfill({ json: { value: 'Current scope B' } });
  for (const old of delayed.filter((r) => r !== current)) {
    await old.fulfill({ json: { value: 'Old scope A' } }).catch(() => {}); // Transport may already be aborted.
  }
  await expect(page.getByTestId('read')).toHaveText('Current scope B');
  await expect(page.getByRole('link', { name: 'First location' })).toHaveAttribute('href', '/__workspace_test?section=first');
  await page.getByRole('button', { name: 'Second section', exact: true }).click();
  await expect(page).toHaveURL(/section=second/); await page.reload();
  await expect(page.getByTestId('section')).toHaveText('second');
  await page.goBack(); await expect(page.getByTestId('section')).toHaveText('first');

  const field = page.getByRole('textbox', { name: 'Exact value', exact: true });
  await expect(field).toHaveAttribute('aria-invalid', 'true');
  const descriptions = await field.getAttribute('aria-describedby');
  assert.equal(descriptions.split(' ').length, 3); await expect(field).toHaveValue(' King ');
  // Playwright rightly refuses ordinary click on aria-disabled; dispatch exercises
  // the real capture/bubble handlers directly, alongside physical keyboard input.
  await page.getByRole('button', { name: 'Disabled action', exact: true }).dispatchEvent('click');
  await page.getByRole('link', { name: 'Disabled link', exact: true }).dispatchEvent('click');
  await page.getByRole('link', { name: 'Disabled link', exact: true }).focus(); await page.keyboard.press('Enter');
  await expect(page.getByTestId('activations')).toHaveText('0'); assert.ok(!page.url().endsWith('#unwanted'));

  const draft = page.getByRole('textbox', { name: 'Retained draft' });
  await draft.fill('Unchanged editor state');
  const mounts = await draft.getAttribute('data-mounts');
  await draft.evaluate((node) => { window.__retainedDraftNode = node; });
  await page.getByRole('button', { name: 'Expand Retained workspace' }).click();
  const expanded = page.getByRole('dialog', { name: 'Retained workspace', exact: true });
  await expect(expanded).toBeVisible();
  const bounds = await expanded.boundingBox(); assert.ok(bounds.width > 1100 && bounds.height > 750);
  await expect(draft).toHaveValue('Unchanged editor state');
  assert.equal(await draft.getAttribute('data-mounts'), mounts);
  assert.equal(await draft.evaluate((node) => node === window.__retainedDraftNode), true);
  await page.getByRole('button', { name: 'Open nested dialog' }).click();
  await expect(page.getByRole('dialog', { name: 'Fixture dialog', exact: true })).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(page.getByRole('dialog', { name: 'Fixture dialog', exact: true })).not.toBeVisible();
  await expect(expanded).toBeVisible();
  await expect(page.getByRole('button', { name: 'Open nested dialog' })).toBeFocused();
  await page.keyboard.press('Escape'); await expect(expanded).not.toBeVisible();
  await expect(page.getByRole('button', { name: 'Expand Retained workspace' })).toBeFocused();
  await expect(draft).toHaveValue('Unchanged editor state');
  assert.equal(await draft.getAttribute('data-mounts'), mounts);
  for (const width of [360, 768, 1280, 1920]) {
    await page.setViewportSize({ width, height: 900 });
    await page.getByRole('button', { name: 'Expand Retained workspace' }).click();
    const box = await page.getByRole('dialog', { name: 'Retained workspace', exact: true }).boundingBox();
    assert.ok(box.width > width * 0.9 && box.x >= 0 && box.x + box.width <= width + 1);
    await page.keyboard.press('Escape');
  }
  assert.deepEqual(errors, []);
  console.log('WORKSPACE_UI_OK scope fencing; non-overlapping polling; independent panes; URL/back/reload; exact accessible fields; disabled activation; retained DOM/editor; nested modal focus/Escape; four viewport widths');
  passed = true;
} finally {
  if (context) await context.tracing.stop({ path: join(artifacts, 'trace.zip') });
  if (browser) await browser.close(); await server.close();
  if (passed) await rm(artifacts, { recursive: true, force: true });
  else console.error(`Workspace browser failure artifacts: ${artifacts}`);
}
