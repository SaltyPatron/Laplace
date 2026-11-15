import { test, expect, type Page } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';

// Point LAPLACE_E2E_URL at Vite; the fixture runs the production event pages against an in-memory API.
test.skip(process.env.LAPLACE_E2E_FIXTURES !== '1', 'requires the Vite-only interaction fixtures');

async function registerAll(page: Page, names: string[]) {
  for (const name of names) {
    await page.getByLabel('Your name').fill(name);
    await page.getByRole('button', { name: 'Register' }).click();
    await expect(page.getByRole('status')).toHaveText(`${name} is registered.`);
  }
}

test('a host creates an event, people register, and results fill the bracket', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto('/tests/events.html');
  await expect(page.getByText('No events yet.')).toBeVisible();
  await page.getByLabel('Title').fill('Friday sweeper');
  await page.getByRole('button', { name: 'Create event' }).click();

  await expect(page.getByRole('heading', { name: 'Friday sweeper' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Start event' })).toBeDisabled();
  await registerAll(page, ['Avery Stone', 'Blake Rivers', 'Casey Lane', 'Devon Pike', 'Emery Wood']);
  await page.getByLabel('Your name').fill('avery stone');
  await page.getByRole('button', { name: 'Register' }).click();
  await expect(page.getByRole('alert')).toHaveText('That name is already registered, or the event is full.');
  await page.getByRole('button', { name: 'Remove Emery Wood' }).click();
  await expect(page.getByRole('heading', { name: 'Entrants (4)' })).toBeVisible();
  expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([]);

  await page.getByRole('button', { name: 'Start event' }).click();
  const final = page.getByRole('article', { name: 'Final', exact: true });
  await expect(final).toContainText('Winner of Semifinal 1');
  await page.getByRole('listitem').filter({ hasText: 'Semifinal 1' }).getByRole('button', { name: 'Enter result' }).click();
  const dialog = page.getByRole('dialog');
  await dialog.getByRole('group', { name: 'Avery Stone' }).getByLabel('Score').fill('187');
  await dialog.getByRole('group', { name: 'Devon Pike' }).getByLabel('Score').fill('262');
  await dialog.getByRole('group', { name: 'Devon Pike' }).getByLabel('Finish').selectOption('1');
  await expect(dialog.getByRole('group', { name: 'Avery Stone' }).getByLabel('Finish')).toHaveValue('2');
  await dialog.getByRole('button', { name: 'Save result' }).click();
  await expect(final).toContainText('Devon Pike');
  await expect(page.getByRole('article', { name: 'Semifinal 1' })).toContainText('262');
  await expect(page.getByRole('listitem').filter({ hasText: 'Semifinal 1' }).getByRole('button', { name: 'Change result' })).toBeVisible();
  expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([]);
});

test('a visitor with the link can register but sees no host controls', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 800 });
  await page.goto('/tests/events.html?view=large&host=0&at=/events/big');
  await expect(page.getByRole('heading', { name: 'Big sweeper' })).toBeVisible();
  await registerAll(page, ['Finley Cross']);
  await expect(page.getByRole('button', { name: 'Start event' })).toHaveCount(0);
  await expect(page.getByRole('button', { name: /^Remove / })).toHaveCount(0);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
  expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([]);
});

test('an event over the free size says it needs the hosting fee before it starts', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto('/tests/events.html?view=large&at=/events/big');
  await expect(page.getByText('This event is over the free size of 8.')).toBeVisible();
  await page.getByRole('button', { name: 'Start event' }).click();
  await expect(page.getByRole('alert')).toHaveText('This event is larger than the free size, and checkout is not set up on this host.');
  await expect(page.getByRole('heading', { name: 'Big sweeper' })).toBeVisible();
});
