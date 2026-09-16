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
  const generatedIds = [];
  for (const [role, name, value] of [
    ['textbox', 'Automatic input', 'preserved'],
    ['combobox', 'Automatic selection', 'one'],
    ['textbox', 'Automatic multiline', 'original text'],
  ]) {
    const control = page.getByRole(role, { name, exact: true });
    await expect(control).toHaveValue(value);
    const id = await control.getAttribute('id');
    assert.ok(id); generatedIds.push(id);
    const description = await control.getAttribute('aria-describedby');
    assert.ok(description);
    assert.equal(await control.evaluate((node) =>
      node.getAttribute('aria-describedby').split(' ').every((part) => document.getElementById(part))), true);
    await page.getByText(name, { exact: true }).click();
    await expect(control).toBeFocused();
  }
  assert.equal(new Set(generatedIds).size, 3, 'automatic field IDs must be unique');
  // Playwright rightly refuses ordinary click on aria-disabled; dispatch exercises
  // the real capture/bubble handlers directly, alongside physical keyboard input.
  await page.getByRole('button', { name: 'Disabled action', exact: true }).dispatchEvent('click');
  await page.getByRole('link', { name: 'Disabled link', exact: true }).dispatchEvent('click');
  const disabledLink = page.getByRole('link', { name: 'Disabled link', exact: true });
  await disabledLink.dispatchEvent('auxclick', { button: 1, bubbles: true, cancelable: true });
  assert.equal(await disabledLink.getAttribute('href'), null, 'disabled native links have no navigable destination');
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
  // Exercise the actual consumers as well as the shared control composition.
  const bodies = [];
  let failQuery = false, heldUsage;
  const invocations = [];
  await page.route('**/v1/**', async (route) => {
    const path = new URL(route.request().url()).pathname;
    if (path === '/v1/ops/catalog') return route.fulfill({ json: { object: 'op.catalog', truncated_at: null, operations: [
      { name: 'ops.fixture_read', args: 'p_id bigint, p_text text, p_optional text DEFAULT NULL', returns: 'TABLE(answer text)', kind: 'function', writable: false, destructive: false,
        parameters: [{ name: 'p_id', type: 'bigint', optional: false }, { name: 'p_text', type: 'text', optional: false }, { name: 'p_optional', type: 'text', optional: true }] },
      { name: 'ops.fixture_write', args: '', returns: 'TABLE(changed boolean)', kind: 'function', writable: true, destructive: false, parameters: [] },
    ] } });
    if (path === '/v1/op') {
      invocations.push(route.request().postData());
      return route.fulfill({ json: { object: 'op.result', name: route.request().postDataJSON().name, rows: [{ answer: 'Operation fixture result' }], truncated_at: null } });
    }
    if (path === '/v1/query/shapes') return route.fulfill({ json: { shapes: [
      { shape: 'describe', summary: 'Fixture description read', needs_topic2: false, needs_type: false, accepts_lang: true },
      { shape: 'path', summary: 'Fixture two-topic read', needs_topic2: true, needs_type: false, accepts_lang: false },
    ] } });
    if (path === '/v1/query') {
      const body = route.request().postDataJSON(); bodies.push(body);
      if (failQuery) return route.fulfill({ status: 503, json: { error: { message: 'Fixture query unavailable' } } });
      return route.fulfill({ json: { shape: body.shape, topic_id: 'a'.repeat(32), topic_label: body.topic, rows: [{ reply: 'Fixture witnessed result', eff_mu: 1, witnesses: 1 }] } });
    }
    if (path === '/v1/billing/plans') return route.fulfill({ json: { data: [{ plan_id: 'fixture', name: 'Fixture plan', monthly_price_cents: 1255, description: 'UI-only fixture', monthly_credits: {} }] } });
    if (path === '/v1/billing/catalog') return route.fulfill({ status: 503, json: { error: { message: 'Fixture catalog unavailable' } } });
    if (path === '/v1/billing/usage') { heldUsage = route; return; }
    // No unexpected fixture action may reach a real server.
    return route.fulfill({ status: 501, json: { error: { message: `Unprovided fixture route: ${path}` } } });
  });
  await page.goto(`${base}/__workspace_test?view=query`);
  await page.getByRole('textbox', { name: 'topic', exact: true }).fill(' King ');
  await expect(page.getByRole('button', { name: 'Run query', exact: true })).toBeEnabled();
  await page.getByRole('button', { name: 'Run query', exact: true }).click();
  await expect(page.getByText('Fixture witnessed result', { exact: true })).toBeVisible();
  assert.equal(bodies.at(-1).topic, ' King ');
  const callsBeforeExpand = bodies.length;
  await page.getByRole('button', { name: 'Expand Result', exact: true }).click();
  await page.keyboard.press('Escape'); assert.equal(bodies.length, callsBeforeExpand);
  failQuery = true;
  await page.getByRole('button', { name: 'Run query', exact: true }).click();
  await expect(page.getByRole('alert')).toContainText('Fixture query unavailable');
  await expect(page.getByText('Fixture witnessed result', { exact: true })).toBeVisible();
  await page.getByRole('combobox', { name: 'shape', exact: true }).selectOption('path');
  await expect(page.getByRole('button', { name: 'Run query', exact: true })).toBeDisabled();
  await page.getByRole('textbox', { name: 'second topic', exact: true }).fill('second');
  await expect(page.getByRole('button', { name: 'Run query', exact: true })).toBeEnabled();

  await page.goto(`${base}/__workspace_test?view=billing`);
  await expect(page.getByRole('heading', { name: 'Fixture plan', exact: true })).toBeVisible();
  await expect(page.getByText('$12.55/mo', { exact: true })).toBeVisible();
  await expect(page.getByRole('alert')).toContainText('Fixture catalog unavailable');
  await expect(page.getByText('No usage recorded for this tenant.', { exact: true })).toHaveCount(0);
  await expect.poll(() => heldUsage !== undefined).toBe(true);
  await heldUsage.fulfill({ json: { entries: [], total_amount_cents: 0 } });
  await expect(page.getByText('No usage recorded for this tenant.', { exact: true })).toBeVisible();
  heldUsage = undefined;
  await page.getByRole('button', { name: 'Change tenant', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Usage — other-scope', exact: true })).toBeVisible();
  await expect(page.getByText('No usage recorded for this tenant.', { exact: true })).toHaveCount(0);
  await expect.poll(() => heldUsage !== undefined).toBe(true);
  await heldUsage.fulfill({ status: 503, json: { error: { message: 'Fixture usage unavailable' } } });
  await expect(page.getByRole('alert').filter({ hasText: 'Fixture usage unavailable' })).toBeVisible();
  await expect(page.getByText('No usage recorded for this tenant.', { exact: true })).toHaveCount(0);
  await page.goto(`${base}/__workspace_test?view=operations`);
  await page.getByRole('button', { name: /^ops\.fixture_read/ }).click();
  await page.getByRole('textbox', { name: 'p_id', exact: true }).fill('9223372036854775807');
  await page.getByRole('textbox', { name: 'p_text', exact: true }).fill(' King ');
  await page.getByRole('button', { name: 'Run operation', exact: true }).click();
  await expect(page.getByRole('region', { name: 'Operation result' })).toContainText('Operation fixture result');
  assert.deepEqual(JSON.parse(invocations.at(-1)).args, { p_id: '9223372036854775807', p_text: ' King ' });
  await page.getByRole('combobox', { name: 'p_optional — input mode', exact: true }).selectOption('null');
  await page.getByRole('button', { name: 'Run operation', exact: true }).click();
  await expect.poll(() => JSON.parse(invocations.at(-1)).args.p_optional).toBe(null);
  await page.getByRole('button', { name: /^ops\.fixture_write/ }).click();
  const beforeReview = invocations.length;
  await page.getByRole('button', { name: 'Review operation', exact: true }).click();
  await expect(page.getByRole('dialog', { name: 'Confirm state-changing operation', exact: true })).toBeVisible();
  assert.equal(invocations.length, beforeReview, 'review must not execute a write');
  await page.getByRole('button', { name: 'Go back', exact: true }).click();
  assert.equal(invocations.length, beforeReview, 'dismissed confirmation must not execute');
  await page.getByRole('button', { name: 'Review operation', exact: true }).click();
  await page.getByRole('button', { name: 'Confirm and run', exact: true }).click();
  await expect.poll(() => invocations.length).toBe(beforeReview + 1);
  assert.equal(JSON.parse(invocations.at(-1)).name, 'ops.fixture_write');
  assert.deepEqual(errors, []);
  console.log('WORKSPACE_UI_OK scope fencing; non-overlapping polling; independent panes; URL/back/reload; exact accessible fields; automatic label/description/focus binding; disabled primary/auxiliary activation; retained DOM/editor; nested modal focus/Escape; four viewport widths; actual Query exact submission/failure retention; actual Billing independent/failed/empty/tenant states; actual operation catalog/exact parameters/default/null/write confirmation');
  passed = true;
} finally {
  if (context) await context.tracing.stop({ path: join(artifacts, 'trace.zip') });
  if (browser) await browser.close(); await server.close();
  if (passed) await rm(artifacts, { recursive: true, force: true });
  else console.error(`Workspace browser failure artifacts: ${artifacts}`);
}
