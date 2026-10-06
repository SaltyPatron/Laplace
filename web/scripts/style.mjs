// Prints the computed colours of the elements on a page of the running site whose text is the given text: what a
// reader actually sees, for checking contrast without a screenshot.
//   node scripts/style.mjs <path> <text>...
import { chromium } from '@playwright/test';

const base = process.env.LAPLACE_E2E_URL ?? 'http://localhost:8080';
const [path, ...texts] = process.argv.slice(2);
const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
await page.goto(base + path, { waitUntil: 'networkidle', timeout: 60_000 });
await page.waitForTimeout(1500);
for (const text of texts) {
  const found = await page.getByText(text, { exact: true }).evaluateAll((els) => els.map((el) => {
    const s = getComputedStyle(el);
    const p = el.closest('a,button') ?? el;
    const ps = getComputedStyle(p);
    return { tag: el.tagName, cls: el.className, color: s.color, opacity: s.opacity, visibility: s.visibility,
             parent: p.tagName, parentCls: String(p.className), parentColor: ps.color, background: ps.backgroundImage || ps.backgroundColor,
             webkitTextFill: s.webkitTextFillColor };
  }));
  console.log(text, JSON.stringify(found, null, 1));
}
await browser.close();
