import { type Page, type Locator, expect } from '@playwright/test';

export class DashboardPage {
  readonly page: Page;
  readonly heading: Locator;

  constructor(page: Page) {
    this.page = page;
    // The app selector or dashboard - after login the user lands somewhere authenticated
    this.heading = page.locator('h1, h2, h3, h4').first();
  }

  async expectLoaded() {
    // After login, we should no longer see the login form
    await expect(this.page.getByRole('textbox', { name: 'Email address' })).not.toBeVisible({ timeout: 10_000 });
    // We should be on an authenticated page
    await expect(this.heading).toBeVisible({ timeout: 10_000 });
  }
}
