import { test, expect } from '@playwright/test';

test.describe('Unauthenticated Access', () => {
  test('visiting root redirects to login when not authenticated', async ({ page }) => {
    await page.goto('/');

    // Should see the login form (email input)
    await expect(page.getByRole('textbox', { name: 'Email address' })).toBeVisible({ timeout: 10_000 });
  });

  test('forgot password page is accessible', async ({ page }) => {
    await page.goto('/forgot-password');
    await page.waitForLoadState('domcontentloaded');

    await expect(page.getByRole('textbox', { name: 'Email address' })).toBeVisible({ timeout: 10_000 });
    await expect(page.getByRole('button', { name: /send|reset/i })).toBeVisible();
  });
});
