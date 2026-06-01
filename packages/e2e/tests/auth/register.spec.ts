import { test, expect } from '@playwright/test';

test.describe('Register Page', () => {
  test('register page renders with form fields', async ({ page }) => {
    await page.goto('/register');
    await page.waitForLoadState('domcontentloaded');

    // Should show registration form
    await expect(page.getByRole('textbox', { name: 'Email address' })).toBeVisible({ timeout: 10_000 });
    await expect(page.getByRole('textbox', { name: 'Password', exact: true })).toBeVisible();
    await expect(page.getByRole('textbox', { name: 'Confirm password' })).toBeVisible();
    await expect(page.getByRole('button', { name: /create account/i })).toBeVisible();
  });

  test('has link back to sign in', async ({ page }) => {
    await page.goto('/register');
    await page.waitForLoadState('domcontentloaded');

    await expect(page.getByText(/sign in|log in|already have an account/i).first()).toBeVisible({ timeout: 10_000 });
  });
});
