import { test, expect } from '@playwright/test';
import { LoginPage } from '../../pages/login.page';

test.describe('Login Form', () => {
  test('shows OTP mode by default', async ({ page }) => {
    const loginPage = new LoginPage(page);
    await loginPage.goto();

    // OTP mode is default: email + Continue button + "Use password instead" link
    await expect(loginPage.emailInput).toBeVisible();
    await expect(loginPage.continueButton).toBeVisible();
    await expect(loginPage.usePasswordLink).toBeVisible();
    // Password field should NOT be visible in OTP mode
    await expect(loginPage.passwordInput).not.toBeVisible();
  });

  test('can toggle between OTP and password modes', async ({ page }) => {
    const loginPage = new LoginPage(page);
    await loginPage.goto();

    // Switch to password mode
    await loginPage.switchToPasswordMode();
    await expect(loginPage.passwordInput).toBeVisible();
    await expect(loginPage.signInButton).toBeVisible();
    await expect(loginPage.forgotPasswordLink).toBeVisible();

    // Switch back to OTP mode
    await loginPage.switchToOtpMode();
    await expect(loginPage.continueButton).toBeVisible();
    await expect(loginPage.passwordInput).not.toBeVisible();
  });

  test('has create account link', async ({ page }) => {
    const loginPage = new LoginPage(page);
    await loginPage.goto();

    await expect(loginPage.createAccountLink).toBeVisible();
  });
});
