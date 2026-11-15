// A stage's layout comes from its match DAG alone: columns by round, parents centred on feeders, nothing overlapping.
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
import test from 'node:test';
const require = createRequire(import.meta.url);
const ts = require('typescript');
const load = async (name, imports = {}) => {
  const source = await readFile(new URL(`../src/compete/${name}.ts`, import.meta.url), 'utf8');
  const compiled = ts.transpileModule(source, { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS } }).outputText;
  const exports = {};
  new Function('require', 'exports', compiled)((id) => imports[id] ?? require(id), exports);
  return exports;
};
const { layoutStage, stageShape, COL, GAP, HEAD, ROW } = await load('layout');
const { singleElimination, doubleElimination, roundRobin, sweeperLadder } = await load('samples');

const stage = (competition, id) => competition.matches.filter((match) => match.stage === id);
const centre = (box) => box.y + box.height / 2;
function assertNoOverlap(layout) {
  for (const column of layout.rounds.keys()) {
    const boxes = layout.boxes.filter((box) => box.column === column).sort((a, b) => a.y - b.y);
    for (let i = 1; i < boxes.length; i++) assert.ok(boxes[i].y >= boxes[i - 1].y + boxes[i - 1].height, `column ${column} overlaps`);
  }
}

test('the shape of a stage is read from its matches, not from a format name', () => {
  assert.equal(stageShape(stage(singleElimination(8, 0), 'main')), 'tree');
  assert.equal(stageShape(stage(roundRobin(6, 0), 'league')), 'pairs');
  assert.equal(stageShape(stage(sweeperLadder(0), 'qualifying')), 'field');
  assert.equal(stageShape(stage(sweeperLadder(0), 'ladder')), 'tree');
  assert.equal(stageShape(stage(doubleElimination(0), 'losers')), 'tree');
});

test('eight entrants: three columns, each match centred on the two that feed it', () => {
  const matches = stage(singleElimination(8, 0), 'main');
  const layout = layoutStage(matches);
  assert.deepEqual(layout.rounds, [1, 2, 3]);
  assert.equal(layout.boxes.length, 7);
  assert.equal(layout.links.length, 6);
  assert.equal(layout.width, 3 * COL + 2 * GAP);
  assertNoOverlap(layout);
  const box = new Map(layout.boxes.map((each) => [each.id, each]));
  for (const match of matches.filter((each) => each.round > 1)) {
    const feeders = match.slots.map((slot) => box.get(slot.from.match));
    assert.ok(Math.abs(centre(box.get(match.id)) - (centre(feeders[0]) + centre(feeders[1])) / 2) < 1e-9);
  }
  const final = layout.links.filter((link) => link.to === 'r3m1');
  assert.deepEqual(final.map((link) => link.y2 - box.get('r3m1').y), [HEAD + 0.5 * ROW, HEAD + 1.5 * ROW]);
});

test('a seed with no opponent has no first-round match and enters the next round directly', () => {
  const competition = singleElimination(6, 0);
  const matches = stage(competition, 'main');
  assert.equal(matches.filter((match) => match.round === 1).length, 2);
  const semis = matches.filter((match) => match.round === 2);
  assert.deepEqual(semis.map((match) => match.slots[0].entrant), ['e1', 'e2']);
  const layout = layoutStage(matches);
  assertNoOverlap(layout);
  const box = new Map(layout.boxes.map((each) => [each.id, each]));
  for (const semi of semis) assert.ok(Math.abs(centre(box.get(semi.id)) - centre(box.get(semi.slots[1].from.match))) < 1e-9);
});

test('matches fed only from another stage stack in order; cross-stage sources draw no connector', () => {
  const losers = stage(doubleElimination(0), 'losers');
  const layout = layoutStage(losers);
  assertNoOverlap(layout);
  assert.deepEqual(layout.rounds, [1, 2, 3, 4]);
  assert.equal(layout.links.length, 5);
  assert.ok(layout.links.every((link) => losers.some((match) => match.id === link.from)));
});

test('results carry forward through the DAG', () => {
  const done = sweeperLadder(5);
  const title = done.matches.find((match) => match.id === 's4');
  assert.equal(title.state, 'final');
  const block = done.matches.find((match) => match.id === 'q');
  assert.equal(title.slots[1].entrant, block.slots.find((slot) => slot.place === 1).entrant);
});
