import { test, expect } from '@playwright/test';
import { LoginPage } from '../../pages/login.page';

test.describe('Password Login', () => {
  test('user can login with password', async ({ page }) => {
    const loginPage = new LoginPage(page);
    await loginPage.goto();

    // Switch to password mode and login
    await loginPage.loginWithPassword('test@test.com', 'Test123!');

    // Should land on an authenticated page (no longer on login)
    await expect(loginPage.emailInput).not.toBeVisible({ timeout: 10_000 });
  });

  test('shows error for invalid credentials', async ({ page }) => {
    const loginPage = new LoginPage(page);
    await loginPage.goto();
    await loginPage.switchToPasswordMode();

    await loginPage.emailInput.fill('test@test.com');
    await loginPage.passwordInput.fill('WrongPassword!');
    await loginPage.signInButton.click();

    // Should show an error message (the app shows "Request failed with status code 400" or similar)
    await expect(page.locator('[role="alert"]')).toBeVisible({ timeout: 5_000 });
  });
});
