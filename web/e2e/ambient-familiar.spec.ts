import { expect, test } from '@playwright/test';

test.describe('ambient familiar', () => {
  test.use({ viewport: { width: 1440, height: 900 } });

  test('responds to explicit visit and swim signals without capturing input', async ({ page }) => {
    await page.goto('/');

    const familiar = page.getByTestId('ambient-familiar');
    await expect(familiar).toBeVisible();
    await expect(familiar).toHaveCSS('pointer-events', 'none');

    await page.evaluate(() => {
      window.dispatchEvent(new CustomEvent('laplace:familiar', { detail: 'visit' }));
    });
    await expect(familiar.locator('[data-mode="climb"]')).toBeVisible();
    await expect(familiar.locator('[data-mode="wander"]')).toBeVisible({ timeout: 2_000 });

    await page.evaluate(() => {
      window.dispatchEvent(new CustomEvent('laplace:familiar', { detail: 'swim' }));
    });
    await expect(familiar.locator('[data-mode="swim"]')).toBeVisible();
  });

  test('does not mount when reduced motion is requested', async ({ page }) => {
    await page.emulateMedia({ reducedMotion: 'reduce' });
    await page.goto('/');
    await expect(page.getByTestId('ambient-familiar')).toHaveCount(0);
  });

  test('sprite atlas keeps transparent corners', async ({ page }) => {
    await page.goto('/');
    const cornerAlpha = await page.evaluate(async () => {
      const image = new Image();
      image.src = '/assets/familiar/laplace-familiar-atlas-v1.png';
      await image.decode();
      const canvas = document.createElement('canvas');
      canvas.width = image.naturalWidth;
      canvas.height = image.naturalHeight;
      const context = canvas.getContext('2d', { willReadFrequently: true });
      if (!context) throw new Error('2D canvas unavailable');
      context.drawImage(image, 0, 0);
      return [
        context.getImageData(0, 0, 1, 1).data[3],
        context.getImageData(canvas.width - 1, 0, 1, 1).data[3],
        context.getImageData(0, canvas.height - 1, 1, 1).data[3],
        context.getImageData(canvas.width - 1, canvas.height - 1, 1, 1).data[3],
      ];
    });

    expect(cornerAlpha).toEqual([0, 0, 0, 0]);
  });
});
