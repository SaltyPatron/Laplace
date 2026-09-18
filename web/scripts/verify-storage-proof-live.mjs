import { chromium } from '@playwright/test';
import { mkdir } from 'node:fs/promises';
import { resolve } from 'node:path';

const apiBase = (process.env.LAPLACE_API_BASE ?? 'http://127.0.0.1:5187').replace(/\/$/, '');
const uiBase = (process.env.LAPLACE_UI_URL ?? 'http://127.0.0.1:8080').replace(/\/$/, '');
const outDir = resolve(process.env.LAPLACE_STORAGE_PROOF_EVIDENCE_DIR ?? '../build/eval-proof');
await mkdir(outDir, { recursive: true });

const proofResponse = await fetch(`${apiBase}/v1/explore/storage-proof`, {
  method: 'POST',
  headers: { 'content-type': 'application/json', 'x-laplace-tenant': 'ci' },
  body: JSON.stringify({ text: 'aa' }),
});
if (!proofResponse.ok) throw new Error(`storage-proof endpoint returned ${proofResponse.status}`);
const proof = await proofResponse.json();

if (proof.perfcache_aligned !== true) {
  throw new Error(`storage proof ROM is not aligned: ${JSON.stringify({
    app: proof.perfcache_receipt_hex,
    db: proof.database_perfcache_receipt_hex,
    error: proof.database_perfcache_error,
  })}`);
}
const root = proof.nodes.find((node) => node.id_hex === proof.root_id_hex);
if (!root) throw new Error(`emitted proof nodes do not contain root identity ${proof.root_id_hex}`);
if (!root.packed_vertices?.length || !root.realized_vertices?.length) {
  throw new Error('root composition has no packed/realized evidence');
}

const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 1600, height: 1000 } });
const consoleErrors = [];
page.on('console', (message) => {
  if (message.type() === 'error') consoleErrors.push(message.text());
});
page.on('pageerror', (error) => consoleErrors.push(error.message));

const screenshot = resolve(outDir, 'storage-proof-live.png');
try {
  await page.goto(`${uiBase}/proof?q=aa`, { waitUntil: 'domcontentloaded', timeout: 20_000 });
  const proofSurface = page.locator('[data-storage-proof-surface="v4"]');
  await proofSurface.waitFor({ state: 'visible', timeout: 20_000 });
  const renderedRoot = await proofSurface.getAttribute('data-storage-proof-root');
  const renderedRom = await proofSurface.getAttribute('data-storage-proof-rom');
  if (renderedRoot !== proof.root_id_hex) {
    throw new Error(`rendered proof root ${renderedRoot} != API root ${proof.root_id_hex}`);
  }
  if ((renderedRom ?? '').toLowerCase() !== proof.perfcache_receipt_hex.toLowerCase()) {
    throw new Error(`rendered proof ROM ${renderedRom} != API ROM ${proof.perfcache_receipt_hex}`);
  }
  await page.getByRole('link', { name: 'Storage Proof', exact: true }).waitFor({ state: 'visible', timeout: 20_000 });
  await page.getByRole('heading', { name: 'Selected storage address' }).waitFor({ state: 'visible', timeout: 20_000 });
  await page.getByText('same exact T0 ROM').waitFor({ state: 'visible', timeout: 20_000 });

  const bodyText = await page.locator('body').innerText();
  if (bodyText.includes('Select a node from the composition walk.')) {
    throw new Error('proof UI failed to select its emitted root');
  }
  if (!bodyText.includes(proof.root_id_hex)) {
    throw new Error(`proof UI does not expose selected root id ${proof.root_id_hex}`);
  }
  if (!bodyText.includes('212-bit carrier projection') || !bodyText.includes('Realized constituent curve')) {
    throw new Error('proof UI omitted packed or realized composition evidence');
  }
  if (bodyText.includes('UNVERIFIED') || bodyText.includes('T0 ROM MISMATCH')) {
    throw new Error('proof UI reports an unverified/mismatched T0 ROM');
  }

  const canvases = page.locator('canvas');
  await canvases.nth(4).waitFor({ state: 'visible', timeout: 20_000 });
  const count = await canvases.count();
  if (count < 5) throw new Error(`expected at least five proof canvases, saw ${count}`);

  const boxes = [];
  for (let i = 0; i < 5; i++) {
    const box = await canvases.nth(i).boundingBox();
    if (!box || box.width < 200 || box.height < 200) {
      throw new Error(`proof canvas ${i} has unusable bounds ${JSON.stringify(box)}`);
    }
    boxes.push({ width: Math.round(box.width), height: Math.round(box.height) });
  }

  if (consoleErrors.length) {
    throw new Error(`proof UI emitted browser errors: ${consoleErrors.join(' | ')}`);
  }

  await page.screenshot({ path: screenshot, fullPage: true, animations: 'disabled' });
  console.log(JSON.stringify({
    root_id_hex: proof.root_id_hex,
    perfcache_receipt_hex: proof.perfcache_receipt_hex,
    nodes: proof.nodes.length,
    packed_vertices: root.packed_vertices.length,
    realized_vertices: root.realized_vertices.length,
    canvases: boxes,
    screenshot,
  }));
} catch (error) {
  await page.screenshot({ path: screenshot, fullPage: true, animations: 'disabled' }).catch(() => {});
  throw error;
} finally {
  await browser.close();
}
