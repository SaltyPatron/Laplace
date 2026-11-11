import { defineConfig } from '@playwright/test';

// LAPLACE_E2E_URL points the suite at a server someone else runs (a deployment, or a
// script that started its own, as test-chess-ui.mjs does). Without it the suite starts
// this checkout's dev server itself and never reuses one already on the port: a server
// left running from another checkout would otherwise answer, and the suite would test
// the wrong code. A busy port is a hard failure, not a silent substitution.
const external = process.env.LAPLACE_E2E_URL;
const port = Number(process.env.LAPLACE_E2E_PORT ?? 5187);
const baseURL = external ?? `http://127.0.0.1:${port}`;

export default defineConfig({
  testDir: './e2e',
  timeout: 30_000,
  retries: 0,
  use: {
    baseURL,
    trace: 'retain-on-failure',
    channel: process.env.PLAYWRIGHT_CHANNEL || undefined,
  },
  webServer: external ? undefined : {
    command: `npx vite --host 127.0.0.1 --port ${port} --strictPort`,
    url: baseURL,
    reuseExistingServer: false,
    timeout: 120_000,
  },
  projects: [
    { name: 'chromium', use: { browserName: 'chromium' } },
  ],
});
