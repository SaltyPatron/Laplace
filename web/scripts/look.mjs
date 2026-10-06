// Opens a page of the running site in Chromium, waits for it to settle, and saves a full-page screenshot and the
// page's visible text, so what the site shows can be read and compared with the source it came from.
//   node scripts/look.mjs <path> <out-prefix> [tab]   e.g. node scripts/look.mjs /explore/entity/<id> D:/Temp/look/lu structure
import { chromium } from '@playwright/test';
import { writeFileSync } from 'node:fs';

const base = process.env.LAPLACE_E2E_URL ?? 'http://localhost:8080';
const [path, out] = process.argv.slice(2);
if (!path || !out) { console.error('usage: node scripts/look.mjs <path> <out-prefix>'); process.exit(2); }

const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
const failures = [];
page.on('response', (r) => { if (r.status() >= 400) failures.push(`${r.status()} ${r.url()}`); });
page.on('pageerror', (e) => failures.push(`page error: ${e.message}`));
await page.goto(base + path, { waitUntil: 'networkidle', timeout: 60_000 });
await page.waitForTimeout(1500);
const tab = process.argv[4];                                   // optional: the tab to open, by its name
if (tab) { await page.getByRole('button', { name: tab, exact: true }).first().click(); await page.waitForLoadState('networkidle'); await page.waitForTimeout(2000); }
await page.screenshot({ path: `${out}.png`, fullPage: true });
const links = await page.$$eval('main a[href], [role=main] a[href], body a[href*="/explore/entity/"]',
  (as) => [...new Set(as.map((a) => `${a.innerText.trim()}\t${a.getAttribute('href')}`))]);
writeFileSync(`${out}.txt`, (await page.innerText('body')) + '\n\n== links\n' + links.join('\n'));
console.log(`${out}.png  ${out}.txt`);
if (failures.length) console.log(failures.join('\n'));
await browser.close();
