// Chat turns have three honest outcomes: answered, abstained (with reason), failed (naming the stage).
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
import test from 'node:test';
const require = createRequire(import.meta.url);
const ts = require('typescript');
const source = await readFile(new URL('../src/chat/turnState.ts', import.meta.url), 'utf8');
const compiled = ts.transpileModule(source, { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS } }).outputText;
const exports = {};
new Function('require', 'exports', compiled)(require, exports);
const { classifyTurn, lastStage } = exports;

const proofSource = await readFile(new URL('../src/api/forwardProof.ts', import.meta.url), 'utf8');
const proofCompiled = ts.transpileModule(proofSource, { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS } }).outputText;
const proofExports = {};
new Function('require', 'exports', proofCompiled)(require, proofExports);
const { summarizeWitnessShares, FIRMWARE_PROFILES, SHARE_BP_TOTAL } = proofExports;

// The Engine's receipt: each stage of its trace is a line that begins with the stage's name.
const proof = (disposition, stages) => ({ session: 's', participant: 'u', prompt_id: 'p', ordinal: 1, turns_before: 0,
  witnessed: false, disposition, steps: [],
  trace: stages.map((stage) => `${stage.toUpperCase()}    what the stage did\n           a continued line`).join('\n') });

test('content is an answer', () => {
  assert.deepEqual(classifyTurn({ content: 'Photosynthesis is…' }), { state: 'answered' });
});

test('streaming is pending, not empty', () => {
  assert.equal(classifyTurn({ content: '', streaming: true }).state, 'pending');
});

test('an empty turn that stopped mid-pipeline is a failure naming the furthest stage, not an abstention', () => {
  // The staging turn for "What is photosynthesis?": receipts through STEER, then nothing.
  const outcome = classifyTurn({ content: '', forwardProof: proof('unresolved', ['resolve', 'orient', 'couple', 'propose', 'scan', 'compose', 'steer']) });
  assert.equal(outcome.state, 'failed');
  assert.equal(outcome.stage, 'steer');
  assert.match(outcome.message, /steer/);
});

test('an explicit abstain disposition is an abstention with its reason', () => {
  const outcome = classifyTurn({ content: '', forwardProof: proof('abstained: no claim above threshold', ['resolve', 'select']) });
  assert.deepEqual(outcome, { state: 'abstained', reason: 'abstained: no claim above threshold' });
});

test('transport errors are failures that keep the message', () => {
  assert.deepEqual(classifyTurn({ content: '', error: 'Response interrupted.' }), { state: 'failed', stage: null, message: 'Response interrupted.' });
});

test('no content and no trace is still reported, never blank', () => {
  const outcome = classifyTurn({ content: '   ' });
  assert.equal(outcome.state, 'failed');
  assert.ok(outcome.message.length > 0);
});

test('last stage follows pipeline order, not event order', () => {
  assert.equal(lastStage(proof('x', ['steer', 'resolve', 'scan'])), 'steer');
  assert.equal(lastStage(proof('x', ['unknown'])), null);
  assert.equal(lastStage(undefined), null);
});

test('firmware profile catalog covers all 7 profiles and share_bp conservation checks out of 10000', () => {
  assert.deepEqual(
    FIRMWARE_PROFILES.map((p) => p.id),
    ['program', 'coding', 'deductive', 'inductive', 'abductive', 'constructive', 'adversarial'],
  );
  assert.equal(SHARE_BP_TOTAL, 10000);
  assert.deepEqual(summarizeWitnessShares([
    { witness: 'a', share_bp: 6250 },
    { witness: 'b', share_bp: 3750 },
  ]), { count: 2, sumBp: 10000, expectedBp: 10000, conserved: true });
  assert.equal(summarizeWitnessShares([{ witness: 'a', share_bp: 5000 }]).conserved, false);
  assert.equal(summarizeWitnessShares([]).conserved, false);
});
