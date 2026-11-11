// Execute the projection from the canvas source without mounting WebGL.
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
import test from 'node:test';
const require = createRequire(import.meta.url);
const ts = require('typescript');
const source = await readFile(new URL('../src/explore/glome/glomeProjection.ts', import.meta.url), 'utf8');
const parsed = ts.createSourceFile('glomeProjection.ts', source, ts.ScriptTarget.Latest, true, ts.ScriptKind.TS);
const selected = parsed.statements.filter((node) =>
  ts.isFunctionDeclaration(node) && (
    node.name?.text === 'placementBallPos'
    || node.name?.text === 'packedDisplayPos'
    || node.name?.text === 'unpackMantissaCoord'
  )
  || ts.isVariableStatement(node) && node.declarationList.declarations.some((decl) => decl.name.getText(parsed) === 'SHELL'));
assert.equal(selected.length, 4, 'load the production functions and display scale');
const compiled = ts.transpileModule(selected.map((node) => node.getText(parsed)).join('\n'), {
  compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.ES2022 },
}).outputText;
const { placementBallPos, packedDisplayPos } = await import(`data:text/javascript;base64,${Buffer.from(compiled).toString('base64')}`);

test('interior radii below two percent remain distinct and proportional', () => {
  for (const radius of [0, 0.001, 0.01, 0.02, 0.04, 0.5, 1]) {
    const point = placementBallPos({ x: 1, y: 0, z: 0, m: 0, radius });
    assert.ok(Math.abs(Math.hypot(...point) - radius * 0.85) < 1e-12);
  }
});
test('four-dimensional orthographic projection preserves centroid linearity under rotation', () => {
  const c1 = { x: 0.5, y: 0.5, z: -0.5, m: 0.5, radius: 1 };
  const c2 = { x: -0.5, y: 0.5, z: 0.5, m: -0.5, radius: 1 };
  const parent = {
    x: (c1.x + c2.x) / 2,
    y: (c1.y + c2.y) / 2,
    z: (c1.z + c2.z) / 2,
    m: (c1.m + c2.m) / 2,
    radius: Math.hypot((c1.x + c2.x) / 2, (c1.y + c2.y) / 2, (c1.z + c2.z) / 2, (c1.m + c2.m) / 2),
  };
  for (const xm of [0, 0.7, 2]) for (const zm of [0, -0.8, 2]) {
    const p1 = placementBallPos(c1, xm, zm);
    const p2 = placementBallPos(c2, xm, zm);
    const pp = placementBallPos(parent, xm, zm);
    assert.ok(Math.abs(pp[0] - (p1[0] + p2[0]) / 2) < 1e-12);
    assert.ok(Math.abs(pp[1] - (p1[1] + p2[1]) / 2) < 1e-12);
    assert.ok(Math.abs(pp[2] - (p1[2] + p2[2]) / 2) < 1e-12);
  }
});
test('missing radius uses the coordinate norm and the origin remains the origin', () => {
  assert.deepEqual(placementBallPos({ x: 0, y: 0, z: 0, m: 0, radius: NaN }), [0, 0, 0]);
  const node = { x: 0.001, y: 0.002, z: 0.003, m: 0, radius: NaN };
  assert.ok(Math.abs(Math.hypot(...placementBallPos(node)) - 0.85 * Math.hypot(node.x, node.y, node.z, node.m)) < 1e-12);
});
test('packedDisplayPos unpacks IEEE-754 0x3fd mantissa-packed BLAKE3 slices into distinct 3D coordinates', () => {
  const makePackedDouble = (bits) => {
    const buf = new ArrayBuffer(8);
    const view = new DataView(buf);
    view.setBigUint64(0, 0x3fd0000000000000n | BigInt(bits), false);
    return view.getFloat64(0, false);
  };
  const low = packedDisplayPos({
    x: makePackedDouble(0n),
    y: makePackedDouble(1n << 42n),
    z: makePackedDouble((1n << 42n) - 1n),
    m: makePackedDouble(1n),
    radius: 1,
  });
  assert.ok(Math.abs(low[0] - (-0.85)) < 1e-9);
  assert.ok(Math.abs(low[1] - 0) < 1e-9);
  assert.ok(Math.abs(low[2] - 0.85) < 1e-6);
});
