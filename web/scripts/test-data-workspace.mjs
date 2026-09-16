// Execute the production response-selection, byte codec and admission controller.
// Substituted providers here do not assert live native/database acceptance.
import assert from 'node:assert/strict';
import test from 'node:test';
import { readFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url);
const ts = require('typescript');
const moduleCache = new Map();
async function moduleUrl(relative) {
  if (moduleCache.has(relative)) return moduleCache.get(relative);
  let source = await readFile(new URL(relative, import.meta.url), 'utf8');
  if (relative.endsWith('/uploadQueue.ts')) source = source.replace("from './content'", `from '${await moduleUrl('../src/data/content.ts')}'`);
  const compiled = ts.transpileModule(source, { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.ES2022 }, reportDiagnostics: true });
  assert.equal(compiled.diagnostics?.filter((item) => item.category === ts.DiagnosticCategory.Error).length ?? 0, 0);
  const url = `data:text/javascript;base64,${Buffer.from(compiled.outputText).toString('base64')}`;
  moduleCache.set(relative, url); return url;
}
const rows = await import(await moduleUrl('../src/ui/lib/resultRows.ts'));
const content = await import(await moduleUrl('../src/data/content.ts'));
const { UploadQueue } = await import(await moduleUrl('../src/data/uploadQueue.ts'));
const { ingestStatusTone, countText } = await import(await moduleUrl('../src/admin/ingestPresentation.ts'));
const deferred = () => { let resolve, reject; const promise = new Promise((yes, no) => { resolve = yes; reject = no; }); return { resolve, reject, promise }; };
const receipt = { file_id: 'a'.repeat(32), document_id: 'b'.repeat(32), content_id: 'c'.repeat(32), metadata_id: 'd'.repeat(32), source_id: 'e'.repeat(32), source: 'fixture', bytes: 1, modality: null };
const file = (name = 'x.txt', read = async () => new TextEncoder().encode('A')) => ({ name, path: name, mode: 'text', bytes: 1, read });

test('identical canonical-looking rows retain independent occurrence ordinals', () => {
  const snapshot = rows.captureRows([{ id: 'same' }, { id: 'same' }], 'Two occurrences');
  const selected = rows.selectReceivedRows([], snapshot, [0, 1], true);
  assert.equal(selected.length, 2); assert.notEqual(selected[0].key, selected[1].key);
  assert.deepEqual(selected.map((item) => item.index), [0, 1]);
});
test('a refreshed response cannot silently rebind the existing selection', () => {
  const a = rows.captureRows([{ value: 'before' }], 'A');
  const b = rows.captureRows([{ value: 'after' }], 'B');
  const selected = rows.selectReceivedRows([], a, [0], true);
  const both = rows.selectReceivedRows(selected, b, [0], true);
  assert.deepEqual(both.map((item) => item.row.value), ['before', 'after']);
  assert.equal(rows.selectReceivedRows(both, b, [0], false)[0].snapshot, a);
});
test('selecting a presentation page does not widen to the other response rows', () => {
  const snapshot = rows.captureRows(Array.from({ length: 90 }, (_, index) => ({ index })), 'Received 90');
  const selected = rows.selectReceivedRows([], snapshot, [25, 26], true);
  const exported = JSON.parse(rows.receivedRowsExport(selected));
  assert.deepEqual(exported.rows.map((item) => item.response_ordinal), [26, 27]);
  assert.equal(exported.snapshots[0].received_row_count, 90);
});
test('export preserves received string precision, empty values, scope and original ordinal', () => {
  const value = { integer: '9223372036854775807', decimal: '0.000000000000000001', text: ' King \r\n', empty: '', null: null, zero: 0, false: false };
  const snapshot = rows.captureRows([value], 'Bounded HTTP response', { requested_limit: 1 });
  const exported = JSON.parse(rows.receivedRowsExport([rows.receivedRow(snapshot, 0)]));
  assert.deepEqual(exported.rows[0].value, value); assert.equal(exported.scope, 'explicit received rows');
  assert.equal(exported.snapshots[0].boundary, snapshot.boundary); assert.deepEqual(exported.snapshots[0].context, { requested_limit: 1 });
});
test('missing fields, null, zero, false and empty text remain distinct', () => {
  assert.equal(rows.valueText(rows.rowValue({}, 'toString')), 'Not returned');
  assert.deepEqual([undefined, null, 0, false, ''].map(rows.valueText), ['Not returned', 'NULL', '0', 'false', 'Empty text']);
  assert.deepEqual(rows.rowFields([{ x: 1 }, { y: 2 }]), ['x', 'y']);
});
test('invalid response ordinals fail instead of selecting some other row', () => {
  const snapshot = rows.captureRows([{}], 'one');
  for (const index of [-1, 1, 0.5, NaN]) assert.throws(() => rows.receivedRow(snapshot, index), RangeError);
});
test('base64 roundtrip is byte-exact across chunk and padding boundaries', () => {
  for (const length of [1, 2, 3, 24575, 24576, 24577, 49155]) {
    const bytes = Uint8Array.from({ length }, (_, index) => index % 256);
    assert.equal(content.encodeContent(bytes), Buffer.from(bytes).toString('base64'));
    assert.deepEqual(content.decodeContent(content.encodeContent(bytes)), bytes);
  }
});
test('UTF-8 BOM, decomposed accents, case, line endings and metadata are not normalized', () => {
  const text = '\ufeff King != king\r\ne\u0301\n'; const bytes = content.textBytes(text);
  const payload = content.contentPayload(' King.txt ', 'folder/King.txt', bytes, '2026-01-01T00:00:00Z');
  assert.deepEqual(content.decodeContent(payload.content_base64), bytes);
  assert.equal(payload.name, ' King.txt '); assert.equal(payload.path, 'folder/King.txt');
  assert.equal(payload.modified_at, '2026-01-01T00:00:00Z');
});
test('invalid UTF-8, unpaired surrogate, empty content and invalid paths are rejected before a request', () => {
  assert.throws(() => content.textBytes('\ud800'));
  assert.throws(() => content.contentPayload('x', 'x', new Uint8Array([0xff])));
  assert.throws(() => content.contentPayload('x', 'x', new Uint8Array()));
  for (const path of ['/x', 'C:\\x', '../x', 'a/../x', 'a//x', ''])
    assert.throws(() => content.contentPayload('x', path, new Uint8Array([65])));
});
test('a successful HTTP status without actual admission IDs is not an admission receipt', () => {
  assert.equal(content.contentReceipt(receipt), receipt);
  for (const value of [null, {}, { ...receipt, content_id: '' }, { ...receipt, bytes: -1 }, { ...receipt, modality: undefined }])
    assert.throws(() => content.contentReceipt(value));
});
test('a new selection performs no provider or file read before explicit start', () => {
  let reads = 0; const queue = new UploadQueue(); queue.add(file('one.txt', async () => { reads++; return new Uint8Array([65]); }));
  assert.equal(reads, 0); assert.equal(queue.getSnapshot().items[0].status, 'queued');
});
test('duplicate starts coalesce work and admit original bytes only once', async () => {
  const queue = new UploadQueue(); const held = deferred(); queue.add(file()); let calls = 0;
  const submit = async (mode, payload) => { calls++; assert.equal(mode, 'text'); assert.equal(payload.content_base64, 'QQ=='); return held.promise; };
  const first = queue.start(submit); const second = queue.start(submit);
  await Promise.resolve(); assert.equal(calls, 1); held.resolve(receipt); await Promise.all([first, second]);
  assert.equal(queue.getSnapshot().items[0].receipt, receipt); assert.equal(queue.getSnapshot().running, false);
});
test('adding files during submission does not widen the frozen selection', async () => {
  const queue = new UploadQueue(); const held = deferred(); queue.add(file('one.txt')); let calls = 0;
  const pending = queue.start(async () => { calls++; return held.promise; }); await Promise.resolve();
  queue.add(file('later.txt')); held.resolve(receipt); await pending;
  assert.equal(calls, 1); assert.deepEqual(queue.getSnapshot().items.map((item) => item.status), ['admitted', 'queued']);
});
test('stop after the current request does not claim that committed work was cancelled', async () => {
  const queue = new UploadQueue(); const held = deferred(); queue.add(file('one')); queue.add(file('two')); let calls = 0;
  const pending = queue.start(async () => { calls++; return held.promise; }); await Promise.resolve(); queue.stop();
  assert.equal(queue.getSnapshot().stopping, true); held.resolve(receipt); await pending;
  assert.equal(calls, 1); assert.deepEqual(queue.getSnapshot().items.map((item) => item.status), ['admitted', 'queued']);
});
test('stop during local read prevents the HTTP submission', async () => {
  const queue = new UploadQueue(); const held = deferred(); queue.add(file('x', () => held.promise)); let calls = 0;
  const pending = queue.start(async () => { calls++; return receipt; }); queue.stop(); held.resolve(new Uint8Array([65])); await pending;
  assert.equal(calls, 0); assert.equal(queue.getSnapshot().items[0].status, 'queued');
});
test('one transport failure stops the batch without retrying or labelling it admitted', async () => {
  const queue = new UploadQueue(); queue.add(file('one')); queue.add(file('two')); let calls = 0;
  await queue.start(async () => { calls++; throw new Error('connection lost'); });
  assert.equal(calls, 1); assert.deepEqual(queue.getSnapshot().items.map((item) => item.status), ['unconfirmed', 'queued']);
  assert.match(queue.getSnapshot().items[0].error, /No automatic retry/);
});
test('invalid local input is distinct from an unconfirmed server effect', async () => {
  const queue = new UploadQueue(); queue.add(file('bad', async () => new Uint8Array([0xff]))); queue.add(file('good')); let calls = 0;
  await queue.start(async () => { calls++; return receipt; });
  assert.equal(calls, 1); assert.deepEqual(queue.getSnapshot().items.map((item) => item.status), ['invalid', 'admitted']);
});
test('explicit requeue does not start a request and selection removal does not retract', async () => {
  const queue = new UploadQueue(); queue.add(file()); let calls = 0;
  await queue.start(async () => { calls++; throw new Error('lost'); }); const id = queue.getSnapshot().items[0].id;
  queue.requeue(id); assert.equal(calls, 1); assert.equal(queue.getSnapshot().items[0].status, 'queued');
  queue.remove(id); assert.equal(calls, 1); assert.equal(queue.getSnapshot().items.length, 0);
});
test('successful skip and absent throughput are not styled as failures or zero counters', () => {
  assert.equal(ingestStatusTone('skipped-complete'), 'ok');
  assert.equal(ingestStatusTone('UNMEASURED'), 'cancelled'); assert.equal(ingestStatusTone('unbaselined'), 'cancelled');
  assert.equal(ingestStatusTone('failed'), 'failed'); assert.equal(countText(null), 'Not recorded'); assert.equal(countText(0), '0');
});
