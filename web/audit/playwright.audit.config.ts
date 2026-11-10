import { defineConfig } from '@playwright/test';

export default defineConfig({
  testDir: '.',
  testMatch: 'live.spec.ts',
  timeout: 60_000,
  retries: 0,
  workers: 2,
  reporter: [['list'], ['json', { outputFile: 'results/live.json' }]],
  use: {
    baseURL: process.env.LAPLACE_AUDIT_URL ?? 'https://dev.hartonomous.com',
    ignoreHTTPSErrors: true,
    viewport: { width: 1440, height: 900 },
    channel: process.env.PLAYWRIGHT_CHANNEL || undefined,
  },
});
