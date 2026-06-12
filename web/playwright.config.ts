import { defineConfig } from '@playwright/test';

// E2E target: the endpoint serving the built SPA (build-web.cmd staged wwwroot).
// e2e-web.cmd starts the endpoint; locally a running serve.cmd is reused.
const baseURL = process.env.LAPLACE_E2E_URL ?? 'http://127.0.0.1:5187';

export default defineConfig({
  testDir: './e2e',
  timeout: 30_000,
  retries: 0,
  use: {
    baseURL,
    trace: 'retain-on-failure',
  },
  projects: [
    { name: 'chromium', use: { browserName: 'chromium' } },
  ],
});
