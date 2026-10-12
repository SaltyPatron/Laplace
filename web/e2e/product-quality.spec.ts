import { test, expect } from '@playwright/test';

// Point LAPLACE_E2E_URL at Vite; fixtures execute production components with explicit HTTP failures.
test.skip(process.env.LAPLACE_E2E_FIXTURES !== '1', 'requires the Vite-only interaction fixtures');
test('route params preserve percent literals after the router decodes them', async ({ page }) => {
  for (const ref of ['%', '100% complete', '%2F', '犬']) {
    await page.goto(`/tests/product-quality.html?view=resolve&ref=${encodeURIComponent(ref)}`);
    await expect(page.getByLabel('Route')).toHaveText(`/explore?${new URLSearchParams({ q: ref })}`);
  }
});
test('independent topic panels show failed reads and successful facts together', async ({ page }) => {
  await page.goto('/tests/product-quality.html?view=topic');
  await expect(page.getByRole('textbox', { name: 'topic', exact: true })).toHaveValue('%');
  await expect(page.getByText('Independent facts loaded')).toBeVisible();
  await expect(page.getByRole('alert')).toHaveCount(5);
  await expect(page.getByText('Loading…', { exact: true })).toHaveCount(0);
});
test('stage failures and absent stages terminate loading', async ({ page }) => {
  await page.goto('/tests/product-quality.html?view=failed-stage');
  await expect(page.getByRole('alert')).toHaveText('Read temporarily unavailable');
  await page.goto('/tests/product-quality.html?view=missing-stage');
  await expect(page.getByText('No catalog stage matches “missing”.')).toBeVisible();
});
test('pulse waits for auth, stops after refusal, and resumes on sign in', async ({ page }) => {
  await page.goto('/tests/product-quality.html?view=pulse');
  await expect(page.getByLabel('Pulse requests')).toHaveText('0');
  await page.getByRole('button', { name: 'Resolve anonymous auth' }).click();
  await expect(page.getByLabel('Pulse requests')).toHaveText('1');
  await page.waitForTimeout(550); // More than five complete polling intervals.
  await expect(page.getByLabel('Pulse requests')).toHaveText('1');
  await page.getByRole('button', { name: 'Sign in fixture' }).click();
  await expect(page.getByLabel('Pulse result')).toHaveText('42');
});
test('trajectory keyboard navigation respects focus and expands packed runs', async ({ page }) => {
  await page.goto('/tests/product-quality.html?view=glome');
  const slider = page.getByRole('slider', { name: 'Ordinal' });
  await expect(slider).toHaveAttribute('max', '5');
  await page.getByRole('textbox', { name: 'Unrelated editor' }).press('ArrowRight');
  await expect(page.getByText(/ord 1/)).toHaveCount(0);
  const trajectory = page.getByRole('group', { name: 'Physicality trajectory navigation' });
  await trajectory.press('ArrowRight');
  await expect(page.getByText('Repeated child · ord 1 · rle 5', { exact: true })).toBeVisible();
  await slider.press('End');
  await expect(page.getByText('Repeated child · ord 5 · rle 5', { exact: true })).toBeVisible();
});
