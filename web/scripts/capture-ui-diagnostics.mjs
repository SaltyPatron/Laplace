import { chromium } from '@playwright/test';
import { mkdir, writeFile } from 'node:fs/promises';
import { resolve } from 'node:path';
import { performance } from 'node:perf_hooks';

const baseUrl = (process.env.LAPLACE_UI_URL ?? 'http://127.0.0.1:5187').replace(/\/$/, '');
const outputDir = resolve(process.env.LAPLACE_UI_DIAGNOSTICS_DIR ?? 'ui-diagnostics');
const navigationTimeout = Number(process.env.LAPLACE_UI_NAVIGATION_TIMEOUT_MS ?? '15000');
const settleTimeout = Number(process.env.LAPLACE_UI_SETTLE_TIMEOUT_MS ?? '5000');
const diagnosticSourceRevision = (process.env.LAPLACE_DIAGNOSTIC_SOURCE_REVISION ?? '').trim() || null;
const deployedRuntimeRevision = (process.env.LAPLACE_DEPLOYED_RUNTIME_REVISION ?? '').trim() || null;
const revisionMatch = diagnosticSourceRevision && deployedRuntimeRevision
  ? diagnosticSourceRevision === deployedRuntimeRevision
  : null;

const defaultRoutes = [
  ['home', '/'],
  ['chat', '/chat'],
  ['query', '/query'],
  ['explore', '/explore'],
  ['data', '/data'],
  ['chess', '/chess'],
  ['play', '/play'],
  ['lab', '/lab'],
  ['billing', '/billing'],
  ['settings', '/settings'],
  ['operator', '/operator'],
];

const routeFilter = (process.env.LAPLACE_UI_ROUTES ?? '')
  .split(',')
  .map((value) => value.trim())
  .filter(Boolean);
const routes = routeFilter.length
  ? defaultRoutes.filter(([name, path]) => routeFilter.includes(name) || routeFilter.includes(path))
  : defaultRoutes;

const viewports = [
  { name: 'desktop', width: 1440, height: 900 },
  { name: 'mobile', width: 390, height: 844 },
];

function fileStem(value) {
  return value.replace(/^\/+/, '').replace(/[^a-zA-Z0-9_-]+/g, '-') || 'home';
}

function escapeHtml(value) {
  return String(value)
    .replaceAll('&', '&amp;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;')
    .replaceAll('"', '&quot;')
    .replaceAll("'", '&#39;');
}

await mkdir(outputDir, { recursive: true });

const observedAt = new Date().toISOString();
const results = [];
let browser;

try {
  browser = await chromium.launch({ headless: true });
  for (const viewport of viewports) {
    const context = await browser.newContext({ viewport: { width: viewport.width, height: viewport.height } });
    for (const [name, route] of routes) {
      const page = await context.newPage();
      const consoleMessages = [];
      const pageErrors = [];
      const requestFailures = [];
      const httpFailures = [];
      const apiTraffic = [];

      page.on('console', (message) => {
        if (message.type() === 'error' || message.type() === 'warning') {
          consoleMessages.push({ type: message.type(), text: message.text() });
        }
      });
      page.on('pageerror', (error) => pageErrors.push({ message: error.message, stack: error.stack ?? null }));
      page.on('requestfailed', (request) => requestFailures.push({
        method: request.method(),
        resourceType: request.resourceType(),
        url: request.url(),
        failure: request.failure()?.errorText ?? 'unknown',
      }));
      page.on('response', (response) => {
        const request = response.request();
        const record = {
          method: request.method(),
          resourceType: request.resourceType(),
          status: response.status(),
          url: response.url(),
          requestId: response.headers()['x-request-id'] ?? null,
        };
        if (response.status() >= 400) httpFailures.push(record);
        try {
          const parsed = new URL(response.url());
          if (parsed.pathname.startsWith('/v1') || parsed.pathname.startsWith('/health') || parsed.pathname.startsWith('/openapi')) {
            apiTraffic.push(record);
          }
        } catch {
          // Ignore malformed/opaque URLs from browser internals.
        }
      });

      const started = performance.now();
      let navigationError = null;
      let settleError = null;
      const url = `${baseUrl}${route}`;
      try {
        await page.goto(url, { waitUntil: 'domcontentloaded', timeout: navigationTimeout });
      } catch (error) {
        navigationError = error instanceof Error ? error.message : String(error);
      }

      try {
        await page.waitForLoadState('networkidle', { timeout: settleTimeout });
      } catch (error) {
        settleError = error instanceof Error ? error.message : String(error);
      }
      try {
        await page.evaluate(async () => {
          if (document.fonts?.ready) await document.fonts.ready;
        });
      } catch {
        // A hard navigation failure may leave no evaluable document.
      }

      let layout = null;
      try {
        layout = await page.evaluate(() => {
          const root = document.documentElement;
          const viewportWidth = root.clientWidth;
          const viewportHeight = root.clientHeight;
          const documentWidth = Math.max(root.scrollWidth, document.body?.scrollWidth ?? 0);
          const documentHeight = Math.max(root.scrollHeight, document.body?.scrollHeight ?? 0);
          const visibleInteractive = Array.from(document.querySelectorAll('a,button,input,select,textarea,[role="button"],[role="tab"]'));
          const clippedInteractiveControls = visibleInteractive.flatMap((element) => {
            const rect = element.getBoundingClientRect();
            const style = getComputedStyle(element);
            if (style.display === 'none' || style.visibility === 'hidden' || rect.width <= 0 || rect.height <= 0) return [];
            const clippedLeft = Math.max(0, -rect.left);
            const clippedRight = Math.max(0, rect.right - viewportWidth);
            const clippedTop = Math.max(0, -rect.top);
            const clippedBottom = Math.max(0, rect.bottom - viewportHeight);
            if (clippedLeft < 1 && clippedRight < 1 && clippedTop < 1 && clippedBottom < 1) return [];
            return [{
              tag: element.tagName.toLowerCase(),
              text: (element.getAttribute('aria-label') || element.textContent || element.getAttribute('placeholder') || '').trim().replace(/\\s+/g, ' ').slice(0, 120),
              clippedLeft: Math.round(clippedLeft),
              clippedRight: Math.round(clippedRight),
              clippedTop: Math.round(clippedTop),
              clippedBottom: Math.round(clippedBottom),
              rect: { left: Math.round(rect.left), top: Math.round(rect.top), right: Math.round(rect.right), bottom: Math.round(rect.bottom), width: Math.round(rect.width), height: Math.round(rect.height) },
            }];
          }).slice(0, 50);
          return {
            viewportWidth,
            viewportHeight,
            documentWidth,
            documentHeight,
            horizontalOverflowPixels: Math.max(0, documentWidth - viewportWidth),
            clippedInteractiveControls,
          };
        });
      } catch {
        // Preserve the rendered evidence even if layout inspection fails.
      }

      const stem = `${viewport.name}-${fileStem(name)}`;
      const viewportShot = `${stem}.png`;
      const fullShot = `${stem}-full.png`;
      const htmlPath = `${stem}.html`;
      let screenshotError = null;
      try {
        await page.screenshot({ path: resolve(outputDir, viewportShot), fullPage: false, animations: 'disabled' });
        await page.screenshot({ path: resolve(outputDir, fullShot), fullPage: true, animations: 'disabled' });
      } catch (error) {
        screenshotError = error instanceof Error ? error.message : String(error);
      }
      try {
        await writeFile(resolve(outputDir, htmlPath), await page.content(), 'utf8');
      } catch {
        // Preserve the rest of the route evidence even if DOM serialization failed.
      }

      results.push({
        name,
        route,
        requestedUrl: url,
        finalUrl: page.url(),
        viewport,
        title: await page.title().catch(() => ''),
        loadMilliseconds: Math.round(performance.now() - started),
        navigationError,
        settleError,
        screenshotError,
        layout,
        consoleMessages,
        pageErrors,
        requestFailures,
        httpFailures,
        apiTraffic,
        artifacts: { viewport: viewportShot, full: fullShot, html: htmlPath },
      });
      await page.close();
    }
    await context.close();
  }
} catch (error) {
  const message = error instanceof Error ? error.stack ?? error.message : String(error);
  await writeFile(resolve(outputDir, 'collector-error.txt'), `${message}\n`, 'utf8');
  throw error;
} finally {
  await browser?.close();
}

const totals = results.reduce((acc, item) => {
  acc.routes += 1;
  acc.navigationErrors += item.navigationError ? 1 : 0;
  acc.consoleErrors += item.consoleMessages.filter((entry) => entry.type === 'error').length;
  acc.consoleWarnings += item.consoleMessages.filter((entry) => entry.type === 'warning').length;
  acc.pageErrors += item.pageErrors.length;
  acc.requestFailures += item.requestFailures.length;
  acc.http4xx += item.httpFailures.filter((entry) => entry.status >= 400 && entry.status < 500).length;
  acc.http5xx += item.httpFailures.filter((entry) => entry.status >= 500).length;
  acc.horizontalOverflowRoutes += (item.layout?.horizontalOverflowPixels ?? 0) > 0 ? 1 : 0;
  acc.clippedInteractiveControls += item.layout?.clippedInteractiveControls?.length ?? 0;
  return acc;
}, { routes: 0, navigationErrors: 0, consoleErrors: 0, consoleWarnings: 0, pageErrors: 0, requestFailures: 0, http4xx: 0, http5xx: 0, horizontalOverflowRoutes: 0, clippedInteractiveControls: 0 });

const summary = {
  schema: 'laplace.ui-diagnostics/v1',
  observedAt,
  baseUrl,
  diagnosticSourceRevision,
  deployedRuntimeRevision,
  revisionMatch,
  totals,
  results,
};
await writeFile(resolve(outputDir, 'summary.json'), JSON.stringify(summary, null, 2) + '\n', 'utf8');
await writeFile(resolve(outputDir, 'network.json'), JSON.stringify(results.map((item) => ({
  name: item.name,
  route: item.route,
  viewport: item.viewport.name,
  apiTraffic: item.apiTraffic,
  httpFailures: item.httpFailures,
  requestFailures: item.requestFailures,
})), null, 2) + '\n', 'utf8');

const worst = [...results].sort((a, b) => {
  const weight = (x) => (x.navigationError ? 1000 : 0) + x.pageErrors.length * 100 + x.httpFailures.filter((r) => r.status >= 500).length * 50 + x.requestFailures.length * 10 + x.consoleMessages.filter((m) => m.type === 'error').length;
  return weight(b) - weight(a);
});
const markdown = [
  '## Laplace UI diagnostics',
  '',
  `- Target: \`${baseUrl}\``,
  `- Diagnostic source revision: **${diagnosticSourceRevision ?? 'unknown'}**`,
  `- Deployed runtime revision: **${deployedRuntimeRevision ?? 'unknown'}**`,
  `- Source/runtime revision match: **${revisionMatch === null ? 'unknown' : revisionMatch ? 'yes' : 'NO'}**`,
  `- Route/viewport captures: **${totals.routes}**`,
  `- Navigation errors: **${totals.navigationErrors}**`,
  `- Browser exceptions: **${totals.pageErrors}**`,
  `- Console errors / warnings: **${totals.consoleErrors} / ${totals.consoleWarnings}**`,
  `- Failed requests: **${totals.requestFailures}**`,
  `- HTTP 4xx / 5xx observed from rendered pages: **${totals.http4xx} / ${totals.http5xx}**`,
  `- Routes with document-level horizontal overflow: **${totals.horizontalOverflowRoutes}**`,
  `- Clipped interactive controls observed: **${totals.clippedInteractiveControls}**`,
  '',
  '### Highest-signal captures',
  '',
  ...worst.slice(0, 8).map((item) => `- \`${item.viewport.name} ${item.route}\`: nav=${item.navigationError ? 'error' : 'ok'}, pageErrors=${item.pageErrors.length}, requestFailures=${item.requestFailures.length}, httpFailures=${item.httpFailures.length}, consoleErrors=${item.consoleMessages.filter((m) => m.type === 'error').length}, clippedControls=${item.layout?.clippedInteractiveControls?.length ?? 0}, horizontalOverflow=${item.layout?.horizontalOverflowPixels ?? 0}px, ${item.loadMilliseconds} ms`),
  '',
  'The artifact contains viewport screenshots, full-page screenshots, serialized DOM, network evidence, and the machine-readable summary.',
  '',
].join('\n');
await writeFile(resolve(outputDir, 'summary.md'), markdown, 'utf8');

const cards = results.map((item) => {
  const errorCount = item.pageErrors.length + item.requestFailures.length + item.httpFailures.filter((r) => r.status >= 500).length + item.consoleMessages.filter((m) => m.type === 'error').length + (item.navigationError ? 1 : 0);
  return `<article><h2>${escapeHtml(item.viewport.name)} · ${escapeHtml(item.route)}</h2><p>${escapeHtml(item.title || '(no title)')} · ${item.loadMilliseconds} ms · ${errorCount} high-signal errors</p><a href="${escapeHtml(item.artifacts.full)}"><img src="${escapeHtml(item.artifacts.viewport)}" alt="${escapeHtml(item.viewport.name)} ${escapeHtml(item.route)} screenshot"></a><details><summary>Runtime evidence</summary><pre>${escapeHtml(JSON.stringify({ navigationError: item.navigationError, settleError: item.settleError, consoleMessages: item.consoleMessages, pageErrors: item.pageErrors, requestFailures: item.requestFailures, httpFailures: item.httpFailures }, null, 2))}</pre></details></article>`;
}).join('\n');
const html = `<!doctype html><html><head><meta charset="utf-8"><title>Laplace UI diagnostics</title><style>body{font:14px system-ui;margin:24px;background:#111;color:#eee}header{margin-bottom:24px}main{display:grid;grid-template-columns:repeat(auto-fit,minmax(360px,1fr));gap:20px}article{background:#1b1b1b;border:1px solid #444;border-radius:10px;padding:14px}img{width:100%;height:auto;border:1px solid #555;background:white}pre{white-space:pre-wrap;overflow-wrap:anywhere}a{color:#9ecbff}</style></head><body><header><h1>Laplace UI diagnostics</h1><p>${escapeHtml(baseUrl)} · ${escapeHtml(observedAt)}</p><p>source=${escapeHtml(diagnosticSourceRevision ?? 'unknown')} · deployed=${escapeHtml(deployedRuntimeRevision ?? 'unknown')} · revision-match=${escapeHtml(revisionMatch === null ? 'unknown' : revisionMatch ? 'yes' : 'NO')}</p><p>navigation=${totals.navigationErrors}, pageErrors=${totals.pageErrors}, requestFailures=${totals.requestFailures}, http5xx=${totals.http5xx}</p></header><main>${cards}</main></body></html>`;
await writeFile(resolve(outputDir, 'index.html'), html, 'utf8');
