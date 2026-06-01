import { type Page, type Locator, expect } from '@playwright/test';

export class LoginPage {
  readonly page: Page;
  readonly emailInput: Locator;
  readonly passwordInput: Locator;
  readonly signInButton: Locator;
  readonly usePasswordLink: Locator;
  readonly useMagicCodeLink: Locator;
  readonly continueButton: Locator;
  readonly createAccountLink: Locator;
  readonly forgotPasswordLink: Locator;

  constructor(page: Page) {
    this.page = page;
    this.emailInput = page.getByRole('textbox', { name: 'Email address' });
    this.passwordInput = page.getByRole('textbox', { name: 'Password' });
    this.signInButton = page.getByRole('button', { name: 'Sign in' });
    this.usePasswordLink = page.getByRole('button', { name: 'Use password instead' });
    this.useMagicCodeLink = page.getByRole('button', { name: 'Use magic code instead' });
    this.continueButton = page.getByRole('button', { name: 'Continue' });
    this.createAccountLink = page.getByText('Create an account');
    this.forgotPasswordLink = page.getByRole('button', { name: 'Forgot password?' });
  }

  async goto() {
    await this.page.goto('/');
    // Wait for login form to render
    await expect(this.emailInput).toBeVisible({ timeout: 10_000 });
  }

  async switchToPasswordMode() {
    await this.usePasswordLink.click();
    await expect(this.passwordInput).toBeVisible();
  }

  async switchToOtpMode() {
    await this.useMagicCodeLink.click();
    await expect(this.continueButton).toBeVisible();
  }

  async loginWithPassword(email: string, password: string) {
    // Make sure we're in password mode
    if (await this.usePasswordLink.isVisible().catch(() => false)) {
      await this.switchToPasswordMode();
    }

    await this.emailInput.fill(email);
    await this.passwordInput.fill(password);
    await this.signInButton.click();
  }
}
