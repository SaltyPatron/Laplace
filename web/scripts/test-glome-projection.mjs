// Execute the projection from the canvas source without mounting WebGL.
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
import test from 'node:test';
const require = createRequire(import.meta.url);
const ts = require('typescript');
const source = await readFile(new URL('../src/explore/glome/GlomeCanvas.tsx', import.meta.url), 'utf8');
const parsed = ts.createSourceFile('GlomeCanvas.tsx', source, ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX);
const selected = parsed.statements.filter((node) =>
  ts.isFunctionDeclaration(node) && node.name?.text === 'placementBallPos'
  || ts.isVariableStatement(node) && node.declarationList.declarations.some((decl) => decl.name.getText(parsed) === 'SHELL'));
assert.equal(selected.length, 2, 'load the production function and its display scale');
const compiled = ts.transpileModule(selected.map((node) => node.getText(parsed)).join('\n'), {
  compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.ES2022 },
}).outputText;
const { placementBallPos } = await import(`data:text/javascript;base64,${Buffer.from(compiled).toString('base64')}`);

test('interior radii below two percent remain distinct and proportional', () => {
  for (const radius of [0, 0.001, 0.01, 0.02, 0.04, 0.5, 1]) {
    const point = placementBallPos({ x: 1, y: 0, z: 0, m: 0, radius });
    assert.ok(Math.abs(Math.hypot(...point) - radius * 0.85) < 1e-12);
  }
});
test('rotating the four-dimensional coordinate preserves display radius', () => {
  const node = { x: 0.002, y: 0.004, z: -0.001, m: 0.005, radius: 0.007 };
  for (const xm of [0, 0.7, 2]) for (const zm of [0, -0.8, 2]) {
    assert.ok(Math.abs(Math.hypot(...placementBallPos(node, xm, zm)) - 0.85 * node.radius) < 1e-12);
  }
});
test('missing radius uses the coordinate norm and the origin remains the origin', () => {
  assert.deepEqual(placementBallPos({ x: 0, y: 0, z: 0, m: 0, radius: NaN }), [0, 0, 0]);
  const node = { x: 0.001, y: 0.002, z: 0.003, m: 0.004, radius: NaN };
  assert.ok(Math.abs(Math.hypot(...placementBallPos(node)) - 0.85 * Math.hypot(node.x, node.y, node.z, node.m)) < 1e-12);
});
