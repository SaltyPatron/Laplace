import type { Page } from '@playwright/test';

export interface StubOptions {
  /** Serve requests as a signed-in development workspace, which shows the Admin destination. */
  signedIn?: boolean;
}

/**
 * Answers every API read the spec does not mock itself, so a page never waits on a backend.
 * Register this first: Playwright runs the most recently registered matching route first,
 * so a spec's own `page.route(...)` calls take precedence over these defaults.
 * Unmocked reads answer 404 JSON, which pages show as an ordinary read failure.
 */
export async function stubApi(page: Page, options: StubOptions = {}) {
  await page.route(
    (url) => url.pathname.startsWith('/v1/') || url.pathname === '/health',
    (route) => route.fulfill({ status: 404, json: { error: { message: 'Not served by this test fixture' } } }),
  );
  await page.route('**/v1/auth/me', (route) => route.fulfill({
    json: { authenticated: false, user: null, providers: [], devPrincipal: options.signedIn ? 'test' : null },
  }));
}
