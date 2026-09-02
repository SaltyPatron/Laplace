// Ephemeral loopback UI server; the selected browser tests mock every chess API.
// Never start an API service, connect to PostgreSQL, or launch a tournament.
import { createServer } from 'vite';
import { spawn } from 'node:child_process';
import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const output = await mkdtemp(join(tmpdir(), 'laplace-chess-ui-'));
const server = await createServer({ server: { host: '127.0.0.1', port: 0, strictPort: true } });
try {
  await server.listen();
  const result = await new Promise((resolve, reject) => {
    const child = spawn(process.execPath, [
      'node_modules/@playwright/test/cli.js', 'test',
      '--grep', 'gauntlet accepts non-default|replay stepping never steals page scroll', `--output=${output}`,
    ], { stdio: 'inherit', env: { ...process.env, LAPLACE_E2E_URL: server.resolvedUrls.local[0] } });
    child.once('error', reject);
    child.once('exit', (code) => resolve(code ?? 1));
  });
  process.exitCode = result;
} finally {
  await server.close();
  // Keep failed traces for diagnosis; no output is written inside the checkout.
  if (process.exitCode === 0) await rm(output, { recursive: true, force: true });
  else console.error(`Browser failure artifacts retained at ${output}`);
}
