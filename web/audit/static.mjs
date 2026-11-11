// Static audit checks over web/src. Emits audit/results/static.json: { id: { pass, value, detail } }.
import { readFileSync, readdirSync, statSync, existsSync, mkdirSync, writeFileSync } from 'node:fs';
import { join, relative, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

const web = join(fileURLToPath(new URL('.', import.meta.url)), '..');
const src = join(web, 'src');
const files = [];
(function walk(dir) {
  for (const name of readdirSync(dir)) {
    const p = join(dir, name);
    if (statSync(p).isDirectory()) walk(p); else files.push(p);
  }
})(src);
const rel = (p) => relative(src, p).split(sep).join('/');
const read = (p) => readFileSync(p, 'utf8');
const css = files.filter((f) => f.endsWith('.css'));
const code = files.filter((f) => /\.(ts|tsx)$/.test(f) && !f.endsWith('types.gen.ts'));
const features = code.filter((f) => !rel(f).startsWith('ui/') && !/\.stories\.tsx$/.test(f));

function count(list, re, skip = () => false) {
  const hits = [];
  for (const f of list) {
    if (skip(f)) continue;
    read(f).split('\n').forEach((line, i) => {
      const m = line.match(re);
      if (m) hits.push(`${rel(f)}:${i + 1}`);
    });
  }
  return hits;
}

const out = {};
const record = (id, hits, limit = 0) => { out[id] = { pass: hits.length <= limit, value: hits.length, detail: hits.slice(0, 15) }; };

record('DS-1', [
  ...count(css, /#[0-9a-fA-F]{3,8}\b|rgba?\(\s*\d|hsla?\(\s*\d/, (f) => rel(f) === 'ui/theme.css'),
  ...count(code, /['"`]#[0-9a-fA-F]{3,8}['"`]/),
]);
record('DS-2', count(css, /font-size:\s*(?!var\()[^;]*\d/));
record('DS-3', count(css, /(?:^|[\s;{])(padding|margin|gap)[a-z-]*:\s*[^;]*\d(rem|px)/, (f) => rel(f) === 'ui/theme.css'));
{
  const bps = new Set();
  for (const f of css) for (const m of read(f).matchAll(/@media[^{]*?width:\s*(\d+(?:\.\d+)?)(px|rem)/g)) bps.add(m[2] === 'rem' ? Number(m[1]) * 16 : Number(m[1]));
  out['DS-4'] = { pass: bps.size <= 3, value: bps.size, detail: [...bps].sort((a, b) => a - b) };
}
record('DS-5', count(features, /<(button|input|select|table)\b/));
// Static inline styles only: a literal value ('0.5rem', 0, 'grid'). Data-bound values (bar widths, rating
// hues, drag positions) are computed per render and belong inline. The reduced-motion override in the
// theme is the one sanctioned !important.
record('DS-6', [...count(code, /style=\{\{\s*[A-Za-z]+:\s*(['"][^'"]*['"]|\d)/), ...count(css, /!important/, (f) => rel(f) === 'ui/theme.css')]);
{
  const dead = ['home/HomeView.tsx', 'home/Scoreboard.tsx', 'home/Leaderboards.tsx', 'topic/TopicView.tsx'].filter((p) => existsSync(join(src, p)));
  out['CODE-1'] = { pass: dead.length === 0, value: dead.length, detail: dead };
}
{
  const big = code.map((f) => [rel(f), read(f).split('\n').length]).filter(([, n]) => n > 400).sort((a, b) => b[1] - a[1]);
  out['CODE-2'] = { pass: big.length === 0, value: big.length, detail: big.map(([f, n]) => `${f} ${n}`) };
}
{
  const p = join(src, 'explore/glome/GlomeCanvas.tsx');
  const bad = existsSync(p) && /51-bit/.test(read(p));
  out['CODE-3'] = { pass: !bad, value: bad ? 1 : 0, detail: bad ? ['explore/glome/GlomeCanvas.tsx still decodes a 51-bit run count'] : [] };
}

mkdirSync(join(web, 'audit/results'), { recursive: true });
writeFileSync(join(web, 'audit/results/static.json'), JSON.stringify(out, null, 1));
for (const [id, r] of Object.entries(out)) console.log(`${r.pass ? 'PASS' : 'FAIL'} ${id} value=${r.value}`);
