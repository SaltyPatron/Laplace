// Executes the production transport/read controller, not a copied implementation.
// HTTP is substituted only at fetch; these tests do not claim native/database proof.
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
import test from 'node:test';
const require = createRequire(import.meta.url);
const ts = require('typescript');
async function load(relative) {
  const source = await readFile(new URL(relative, import.meta.url), 'utf8');
  const output = ts.transpileModule(source, {
    compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.ES2022 },
    reportDiagnostics: true,
  });
  assert.equal(output.diagnostics?.filter((d) => d.category === ts.DiagnosticCategory.Error).length ?? 0, 0);
  return import(`data:text/javascript;base64,${Buffer.from(output.outputText).toString('base64')}`);
}
const { ReadResource } = await load('../src/ui/lib/readResource.ts');
const { apiGet, apiPost, apiPutText, ApiError, PaymentRequiredError } = await load('../src/api/client.ts');
const deferred = () => {
  let resolve, reject;
  const promise = new Promise((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
};
const tick = () => Promise.resolve();

test('idle cancellation does not create a false cancelled initial state', () => {
  const read = new ReadResource(); const before = read.getSnapshot();
  read.cancel(); assert.equal(read.getSnapshot(), before);
});
test('simultaneous refresh requests share one provider call and one promise', async () => {
  const read = new ReadResource(); const gate = deferred(); let calls = 0;
  const loader = () => { calls++; return gate.promise; };
  const first = read.load(loader); const second = read.load(loader);
  assert.equal(first, second); assert.equal(read.getSnapshot().status, 'loading');
  await tick(); assert.equal(calls, 1); gate.resolve(['same']); await first;
  assert.deepEqual(read.getSnapshot().data, ['same']); assert.equal(read.getSnapshot().status, 'ready');
});
test('cancel before provider invocation performs no provider work', async () => {
  const read = new ReadResource(); let called = false;
  const pending = read.load(async () => { called = true; return 1; });
  read.cancel(); await pending;
  assert.equal(called, false); assert.equal(read.getSnapshot().status, 'cancelled');
});
test('late result from provider ignoring AbortSignal cannot replace a newer result', async () => {
  const read = new ReadResource(); const old = deferred(); let signal;
  const pending = read.load((current) => { signal = current; return old.promise; });
  await tick(); await read.reload(async () => 'new');
  assert.equal(signal.aborted, true); old.resolve('old'); await pending;
  assert.equal(read.getSnapshot().data, 'new');
});
test('late provider failure cannot overwrite current success', async () => {
  const read = new ReadResource(); const old = deferred();
  const pending = read.load(() => old.promise); await tick();
  await read.reload(async () => 'current'); old.reject(new Error('old failure')); await pending;
  assert.equal(read.getSnapshot().status, 'ready'); assert.equal(read.getSnapshot().error, null);
});
test('failed refresh retains exact previous data and observation time', async () => {
  const read = new ReadResource(); const body = { rows: [] };
  await read.load(async () => body); const observedAt = read.getSnapshot().updatedAt;
  const gate = deferred(); const pending = read.load(() => gate.promise);
  assert.equal(read.getSnapshot().status, 'refreshing'); await tick(); gate.reject(new Error('offline')); await pending;
  assert.equal(read.getSnapshot().status, 'failed'); assert.equal(read.getSnapshot().data, body);
  assert.equal(read.getSnapshot().updatedAt, observedAt); assert.equal(read.getSnapshot().error.message, 'offline');
});
test('successful empty and false/zero bodies remain successful data, not missing state', async () => {
  for (const body of [[], 0, false, null, '']) {
    const read = new ReadResource(); await read.load(async () => body);
    assert.equal(read.getSnapshot().status, 'ready'); assert.equal(read.getSnapshot().data, body);
    assert.equal(typeof read.getSnapshot().updatedAt, 'number');
  }
});
test('synchronous provider exception releases the request for retry', async () => {
  const read = new ReadResource(); await read.load(() => { throw new Error('invalid'); });
  assert.equal(read.getSnapshot().status, 'failed'); await read.load(async () => 7);
  assert.equal(read.getSnapshot().data, 7);
});
test('new scope is empty immediately while old scope is pending', async () => {
  const a = new ReadResource(); const b = new ReadResource();
  await a.load(async () => 'tenant A'); assert.equal(b.getSnapshot().data, undefined);
  assert.equal(b.getSnapshot().status, 'idle');
});
test('subscribers receive transitions and unsubscribe cleanly', async () => {
  const read = new ReadResource(); const states = [];
  const unsubscribe = read.subscribe(() => states.push(read.getSnapshot().status));
  await read.load(async () => 1); unsubscribe(); await read.load(async () => 2);
  assert.deepEqual(states, ['loading', 'ready']);
});
test('reentrant loading notification cannot start another request', async () => {
  const read = new ReadResource(); let calls = 0;
  const loader = async () => ++calls;
  read.subscribe(() => { if (read.getSnapshot().status === 'loading') void read.load(loader); });
  await read.load(loader); assert.equal(calls, 1);
});
test('stopping a refresh retains its last successful body', async () => {
  const read = new ReadResource(); await read.load(async () => 'retained');
  const pending = read.load(async () => 'late'); read.cancel(); await pending;
  assert.equal(read.getSnapshot().status, 'cancelled'); assert.equal(read.getSnapshot().data, 'retained');
});

// Tests run serially: each substituted fetch is restored even when an assertion fails.
async function withFetch(fetcher, exercise) {
  const original = globalThis.fetch; globalThis.fetch = fetcher;
  try { await exercise(); } finally { globalThis.fetch = original; }
}
test('all JSON transports forward AbortSignal and declared request scope', async () => {
  const signal = new AbortController().signal; const calls = [];
  await withFetch(async (path, init) => { calls.push({ path, init }); return Response.json({ ok: true }); }, async () => {
    const opts = { signal, tenant: 'A', quoteId: 'Q', session: 'S', operatorToken: 'protected' };
    await apiGet('/read', opts); await apiPost('/write', { text: ' King ' }, opts); await apiPutText('/config', ' { "a":1 }\n', opts);
  });
  assert.equal(calls.length, 3);
  for (const { init } of calls) {
    assert.equal(init.signal, signal); assert.equal(init.headers['X-Laplace-Tenant'], 'A');
    assert.equal(init.headers['X-Laplace-Quote-Id'], 'Q');
  }
  assert.equal(calls[1].init.body, '{"text":" King "}'); assert.equal(calls[2].init.body, ' { "a":1 }\n');
});
test('204 is a completed no-body response rather than a JSON parsing failure', async () => {
  await withFetch(async () => new Response(null, { status: 204 }), async () => assert.equal(await apiPost('/write', {}), undefined));
});
test('Problem Details and request identifier survive transport errors', async () => {
  await withFetch(async () => Response.json({ detail: 'A required field is missing.' }, { status: 400, headers: { 'x-request-id': 'receipt-1' } }), async () => {
    await assert.rejects(apiGet('/bad'), (error) => error instanceof ApiError && error.status === 400 && error.requestId === 'receipt-1' && error.message === 'A required field is missing.');
  });
});
test('malformed 402 body is an ApiError, not a secondary TypeError', async () => {
  await withFetch(async () => Response.json({ unrelated: true }, { status: 402 }), async () => {
    await assert.rejects(apiGet('/bad'), (error) => error instanceof ApiError && error.status === 402);
  });
});
test('documented payment error preserves its typed payload', async () => {
  await withFetch(async () => Response.json({ error: { message: 'A quote is required.' } }, { status: 402 }), async () => {
    await assert.rejects(apiGet('/quote'), (error) => error instanceof PaymentRequiredError && error.message === 'A quote is required.');
  });
});
test('network and write failure are never silently retried', async () => {
  let calls = 0;
  await withFetch(async () => { calls++; throw new Error('network down'); }, async () => {
    await assert.rejects(apiPost('/write', {}), /network down/);
  });
  assert.equal(calls, 1);
});
