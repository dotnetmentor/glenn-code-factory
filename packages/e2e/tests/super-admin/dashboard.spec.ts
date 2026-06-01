import { test, expect } from '../../fixtures/base';

test.describe('Super Admin Dashboard', () => {
  test('super-admin can access the app after auth setup', async ({ page }) => {
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');

    // Should NOT see login form (we're authenticated via storageState)
    await expect(page.getByRole('textbox', { name: 'Email address' })).not.toBeVisible({ timeout: 10_000 });
  });
});
