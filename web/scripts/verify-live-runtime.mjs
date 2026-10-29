// Read the deployed application through a real browser; no API responses are mocked.
// The caller owns credential issuance/revocation and the result directory.
import { chromium, expect } from '@playwright/test';
import { mkdir, writeFile } from 'node:fs/promises';
import { isAbsolute, join } from 'node:path';
import { parseArgs } from 'node:util';

const { values } = parseArgs({ options: {
  base: { type: 'string' }, output: { type: 'string' }, channel: { type: 'string' }, revision: { type: 'string' },
} });
const base = new URL(values.base);
if (base.pathname !== '/' || base.search || base.hash || base.username || base.password ||
    (base.protocol !== 'https:' && !(base.protocol === 'http:' && ['localhost', '127.0.0.1'].includes(base.hostname)))) {
  throw new Error('Declare an HTTPS application origin, or a loopback development origin.');
}
if (!values.output || !isAbsolute(values.output)) throw new Error('Declare an absolute result directory.');
const key = process.env.LAPLACE_API_KEY;
if (!key?.startsWith('sk-laplace-')) throw new Error('An enrolled verification API key is required.');
const scrub = (message) => String(message).replaceAll(key, '[credential]');
const report = { origin: base.origin, started: new Date().toISOString(), passed: false,
  deployed_revision: values.revision ?? null, browser: null, pages: [], errors: [] };
await mkdir(values.output, { recursive: true });
let browser;
try {
  browser = await chromium.launch({ channel: values.channel || undefined, headless: true });
  report.browser = browser.version();
  const context = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
  // Never send a workload credential to external assets or identity providers.
  await context.route('**/*', async (route) => {
    const url = new URL(route.request().url());
    if (url.origin === base.origin && (url.pathname.startsWith('/v1/') || url.pathname.startsWith('/chess/'))) {
      await route.continue({ headers: { ...route.request().headers(), authorization: `Bearer ${key}` } });
    } else await route.continue();
  });
  for (const scenario of [
    { name: 'entity', path: '/explore/entity/a929c9834b79516e7c85fa83e7dac137',
      requests: ['/v1/explore/entities/a929c9834b79516e7c85fa83e7dac137/preview',
        '/v1/explore/entities/a929c9834b79516e7c85fa83e7dac137'], heading: 'Argentina' },
    { name: 'matchup', path: '/explore/matchup/pancake/quark',
      requests: ['/v1/explore/matchup', '/v1/explore/matchup/verdict'] },
  ]) {
    const page = await context.newPage();
    page.setDefaultTimeout(30_000);
    const entry = { name: scenario.name, path: scenario.path, responses: [], errors: [] };
    report.pages.push(entry);
    page.on('pageerror', (error) => entry.errors.push({ kind: 'javascript', message: scrub(error.message) }));
    page.on('console', (message) => {
      if (message.type() === 'error') entry.errors.push({ kind: 'console', message: scrub(message.text()) });
    });
    page.on('requestfailed', (request) => {
      if (request.failure()?.errorText !== 'net::ERR_ABORTED') entry.errors.push({
        kind: 'transport', url: new URL(request.url()).pathname, message: scrub(request.failure()?.errorText),
      });
    });
    page.on('response', (response) => {
      const url = new URL(response.url());
      if (url.origin !== base.origin) return;
      if (url.pathname.startsWith('/v1/') || response.status() >= 400) {
        entry.responses.push({ path: url.pathname, status: response.status() });
      }
      if (response.status() >= 400) entry.errors.push({ kind: 'http', path: url.pathname, status: response.status() });
    });
    const reads = scenario.requests.map((path) => page.waitForResponse((response) => {
      const url = new URL(response.url());
      return url.origin === base.origin && url.pathname === path;
    }, { timeout: 30_000 }).then(async (response) => {
      if (response.status() !== 200) throw new Error(`${path} returned HTTP ${response.status()}`);
      if (!response.headers()['content-type']?.includes('application/json')) throw new Error(`${path} did not return JSON`);
      const failure = await response.finished();
      if (failure) throw failure;
    }));
    // Observe early failures even while navigation is still completing.
    for (const read of reads) void read.catch(() => {});
    try {
      const navigation = await page.goto(new URL(scenario.path, base).href, { waitUntil: 'domcontentloaded' });
      if (navigation?.status() !== 200) throw new Error('The deployed page did not return HTTP 200.');
      await Promise.all(reads);
      if (scenario.heading) {
        await expect(page.getByRole('heading', { name: scenario.heading, exact: true })).toBeVisible();
        await expect(page.getByRole('heading', { name: 'Browse relations', exact: true })).toBeVisible();
        await expect(page.getByRole('heading', { name: 'Salient facts', exact: true })).toBeVisible();
      }
      else {
        await expect(page.getByRole('textbox', { name: 'first entity', exact: true })).toHaveValue('pancake');
        await expect(page.getByRole('textbox', { name: 'second entity', exact: true })).toHaveValue('quark');
        await expect(page.getByText('Reading the evidence…', { exact: true })).toHaveCount(0);
        await expect(page.getByRole('link', { name: 'pancake', exact: true })).toBeVisible();
        await expect(page.getByRole('link', { name: 'quark', exact: true })).toBeVisible();
      }
      entry.headings = await page.getByRole('heading').allTextContents();
      if (entry.errors.length) throw new Error(`${entry.errors.length} browser or request errors`);
    } catch (error) {
      entry.errors.push({ kind: 'verification', message: scrub(error.message) });
      await Promise.allSettled(reads);
    } finally {
      await page.screenshot({ path: join(values.output, `${scenario.name}.png`), fullPage: true });
      await page.close();
    }
  }
  report.passed = report.pages.every((page) => page.errors.length === 0);
} catch (error) {
  report.errors.push(scrub(error.message));
} finally {
  if (browser) await browser.close();
  report.completed = new Date().toISOString();
  await writeFile(join(values.output, 'browser.json'), JSON.stringify(report, null, 2));
}
console.log(JSON.stringify(report, null, 2));
if (!report.passed) process.exitCode = 1;
