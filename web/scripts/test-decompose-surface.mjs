import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
import test from 'node:test';
const require = createRequire(import.meta.url);
const ts = require('typescript');
const source = await readFile(new URL('../src/explore/entity/decomposeSurface.ts', import.meta.url), 'utf8');
const compiled = ts.transpileModule(source, {
  compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS },
}).outputText;
const exports = {};
new Function('require', 'exports', compiled)(require, exports);
const { decomposableSurface, canDecomposeTextSurface, decompositionMatchesEntity } = exports;
const id = 'cccc81128cc88b28208ece2eb5a2c6d0';

test('a text entity at or below sentence tier decomposes its own surface', () => {
  assert.equal(decomposableSurface({ id_hex: id, label: 'break%2:30:04::', tier: 3, type: 'Sentence' }), 'break%2:30:04::');
  assert.equal(decomposableSurface({ id_hex: id, label: 'break', tier: 2, type: 'Word' }), 'break');
});
test('a display label that is not content is never decomposed', () => {
  // a concept labelled by its lexicalization
  assert.equal(canDecomposeTextSurface({ id_hex: id, label: 'break', tier: 2, type: 'ILI' }), false);
  // a record or row labelled by its parts
  assert.equal(canDecomposeTextSurface({ id_hex: id, label: 'break \u00b7 1 \u00b7 11', tier: 4, type: 'Document' }), false);
  // a typed short-id fallback
  assert.equal(canDecomposeTextSurface({ id_hex: id, label: 'WordNet Sense \u00b7 cccc81128cc8', tier: 3, type: 'Sentence' }), false);
  // above sentence tier, the label is a bounded preview of the first child only
  assert.equal(canDecomposeTextSurface({ id_hex: id, label: 'In the beginning', tier: 4, type: 'Document' }), false);
  // unknown type or tier is not assumed to be text
  assert.equal(canDecomposeTextSurface({ id_hex: id, label: 'break' }), false);
});
test('a decomposition is shown only when it recomposes to the entity', () => {
  assert.equal(decompositionMatchesEntity(id, id.toUpperCase()), true);
  assert.equal(decompositionMatchesEntity(id, '195d0ae5e6f1cc1cd65a2e4ee31db309'), false);
  assert.equal(decompositionMatchesEntity(id, undefined), false);
});
