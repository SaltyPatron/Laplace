// Moves are shown in standard algebraic notation (e4, Nf3, exd5, O-O, e8=Q+), never as e2e4.
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
import test from 'node:test';
const require = createRequire(import.meta.url);
const ts = require('typescript');
const source = await readFile(new URL('../src/chess/san.ts', import.meta.url), 'utf8');
const compiled = ts.transpileModule(source, { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS } }).outputText;
const exports = {};
new Function('require', 'exports', compiled)(require, exports);
const { toSan, sanLine } = exports;
const START = 'rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1';

test('pawn and piece moves', () => {
  assert.equal(toSan(START, 'e2e4'), 'e4');
  assert.equal(toSan(START, 'g1f3'), 'Nf3');
});

test('a line from the start: captures, castling, check', () => {
  assert.deepEqual(
    sanLine(START, ['e2e4', 'd7d5', 'e4d5', 'd8d5', 'b1c3', 'd5a5', 'f1c4', 'g8f6', 'g1f3', 'c8g4', 'e1g1']),
    ['e4', 'd5', 'exd5', 'Qxd5', 'Nc3', 'Qa5', 'Bc4', 'Nf6', 'Nf3', 'Bg4', 'O-O'],
  );
  assert.deepEqual(sanLine(START, ['e2e4', 'f7f6', 'd1h5']), ['e4', 'f6', 'Qh5+']);
});

test('disambiguation by file and rank', () => {
  assert.equal(toSan('4k2K/8/8/8/8/8/8/R6R w - - 0 1', 'a1d1'), 'Rad1');
  assert.equal(toSan('4k3/8/8/8/R7/8/8/R3K3 w - - 0 1', 'a1a2'), 'R1a2');
});

test('promotion, en passant and mate', () => {
  assert.equal(toSan('8/4P3/8/8/8/8/8/k6K w - - 0 1', 'e7e8q'), 'e8=Q');
  assert.equal(toSan('4k3/8/8/3pP3/8/8/8/4K3 w - d6 0 1', 'e5d6'), 'exd6');
  assert.equal(toSan('6k1/5ppp/8/8/8/8/8/R5K1 w - - 0 1', 'a1a8', 'checkmate'), 'Ra8#');
});

test('anything that is not a move passes through', () => {
  assert.equal(toSan(START, '(none)'), '(none)');
});
