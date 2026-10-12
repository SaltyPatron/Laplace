import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
import test from 'node:test';
const require = createRequire(import.meta.url);
const ts = require('typescript');
const source = await readFile(new URL('../src/layout/useRouteText.ts', import.meta.url), 'utf8');
const compiled = ts.transpileModule(source, {
  compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS },
}).outputText;
const exports = {};
new Function('require', 'exports', compiled)(require, exports);
const { routeText, decodeRouteText } = exports;

test('text routes preserve literal percent, escape sequences, separators and Unicode', () => {
  for (const text of ['%', '100% complete', '%2F', '%2f', '%25', '/', 'a/b', '犬', 'a#b?c']) {
    assert.equal(routeText('/topic/:ref', `/topic/${encodeURIComponent(text)}`, 'ref'), text);
  }
});
test('each matchup side decodes independently and unmatched routes remain empty', () => {
  const pattern = '/explore/matchup/:x/:y';
  assert.equal(routeText(pattern, '/explore/matchup/%252F/%25', 'x'), '%2F');
  assert.equal(routeText(pattern, '/explore/matchup/%252F/%25', 'y'), '%');
  assert.equal(routeText(pattern, '/explore/matchup', 'x'), '');
});
test('malformed user-authored escapes stay visible instead of throwing', () => {
  for (const text of ['%', '%broken', '%E0%A4%A']) assert.equal(decodeRouteText(text), text);
});
