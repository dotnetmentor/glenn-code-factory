# Page Object Model + Fixture Pattern

## Table of Contents
- [Architecture Overview](#architecture-overview)
- [Writing a Page Object](#writing-a-page-object)
- [API Response Interception in POMs](#api-response-interception-in-poms)
- [Registering in Fixtures](#registering-in-fixtures)
- [Admin Fixture](#admin-fixture)
- [Using in Tests](#using-in-tests)
- [Adding a New Feature's Tests](#adding-a-new-features-tests)

## Architecture Overview

Three layers, each with a clear job:

```
React Components (data-testid)  →  Page Objects (locators + actions)  →  Tests (intent only)
```

- **Components** own the `data-testid` contract
- **Page Objects** own the locators, waiting logic, and API response validation
- **Tests** own the user intent (5-15 lines, reads like English)

## Writing a Page Object

```ts
// pages/booking.page.ts
import { type Page, type Locator, expect } from '@playwright/test';

export class BookingPage {
  readonly page: Page;

  // Store locators as readonly properties — one place to update if selectors change
  readonly confirmationDialog: Locator;
  readonly confirmButton: Locator;
  readonly successTitle: Locator;
  readonly calendarGrid: Locator;

  constructor(page: Page) {
    this.page = page;

    // Use data-testid for structural elements
    this.confirmationDialog = page.getByTestId('booking-confirmation-dialog');
    this.confirmButton = page.getByTestId('booking-confirm-btn');
    this.successTitle = page.getByTestId('booking-success-title');
    this.calendarGrid = page.getByTestId('calendar-grid');
  }

  /** Methods encapsulate waiting — the test never waits manually */
  async selectSlot(slot: Locator) {
    await slot.click();
    // Web-first assertion: auto-retries until dialog is visible
    await expect(this.confirmationDialog).toBeVisible();
  }

  /** Wait for a loading transition to complete */
  async waitForCalendarLoaded() {
    // Wait for EITHER content OR empty state — both prove loading is done
    const loaded = this.calendarGrid.or(this.page.getByText('Inga lediga tider'));
    await expect(loaded).toBeVisible();
  }
}
```

**Key patterns:**
- `readonly` locator properties in constructor — single source of truth
- Methods contain their own waiting (web-first assertions)
- Methods named as user actions: `selectSlot`, `confirmBooking`, `goToActivities`
- Throw descriptive errors when things go wrong
- Use `locatorA.or(locatorB)` for loading state transitions

## API Response Interception in POMs

For any POM method that triggers an API call, **intercept the response**. This is the most reliable CRUD pattern — it eliminates the gap between "API done" and "DOM updated."

```ts
async confirmBooking() {
  await expect(this.confirmButton).toBeEnabled();

  // 1. Listen for API response BEFORE clicking
  const responsePromise = this.page.waitForResponse(
    (r) => r.url().includes('/resource-bookings') && r.request().method() === 'POST',
  );

  // 2. Perform the action
  await this.confirmButton.click();

  // 3. Wait for and validate API response (source of truth)
  const response = await responsePromise;

  if (!response.ok()) {
    const body = await response.json();
    throw new Error(`Booking API failed (${response.status()}): ${body.error ?? body.title}`);
  }

  const body = await response.json();
  if (body.paymentLinkUrl) {
    throw new Error('Booking requires payment redirect — test user should have free access');
  }

  // 4. Only THEN assert on DOM (which should now be updated)
  await expect(this.successTitle).toBeVisible();
}
```

**Why this pattern is critical:**
- Without it: clicking "Confirm" and checking for success text might pass even if the API failed (stale UI)
- With it: you KNOW the API succeeded, and the error message tells you exactly what went wrong
- Use for: creating, updating, deleting — any mutation that hits the backend

**When to use API interception vs. pure DOM assertions:**

| Scenario | Approach |
|----------|----------|
| Form submit (create/update/delete) | API interception + DOM assertion |
| Page navigation | DOM assertion only (`expect(heading).toBeVisible()`) |
| View switching (tabs, calendar views) | DOM assertion (`expect(grid.or(emptyState)).toBeVisible()`) |
| Loading data on page load | API interception if data determines what to test next |

## Registering in Fixtures

```ts
// fixtures/base.ts — for member-authenticated tests
import { test as base, expect } from '@playwright/test';
import { MemberPortalPage } from '../pages/member-portal.page';
import { BookingPage } from '../pages/booking.page';

export const test = base.extend<{
  memberPortal: MemberPortalPage;
  bookingPage: BookingPage;
}>({
  // Fixture with setup: navigates + verifies auth automatically
  memberPortal: async ({ page }, use) => {
    const portal = new MemberPortalPage(page);
    await portal.goto();
    await portal.expectAuthenticated();
    await use(portal);  // Test runs here
    // Teardown runs after (if needed)
  },

  // Simple fixture: just creates the POM
  bookingPage: async ({ page }, use) => {
    await use(new BookingPage(page));
  },
});

export { expect };
```

**Why fixtures over `beforeEach`:**
- On-demand: only created if the test requests the fixture
- Encapsulated: setup + teardown in one place
- Composable: fixtures can depend on each other
- Type-safe: full TypeScript inference

## Admin Fixture

```ts
// fixtures/admin.ts — for tenant-admin tests
import { test as base, expect } from '@playwright/test';

export const test = base.extend<{
  adminPage: {
    goto: (path: string) => Promise<void>;
    page: Page;
  };
}>({
  adminPage: async ({ page }, use) => {
    const adminPage = {
      page,
      goto: async (path: string) => {
        const fullPath = `/t/e2e-test${path.startsWith('/') ? path : '/' + path}`;
        await page.goto(fullPath);
        // Wait for the page content to load
        await page.waitForLoadState('domcontentloaded');
      },
    };
    await use(adminPage);
  },
});

export { expect };
```

**Usage:** Admin tests import from `fixtures/admin.ts` and use `storageState: '.auth/admin.json'` in the config project.

## Using in Tests

```ts
// tests/booking/create-booking.spec.ts
import { test, expect } from '../../fixtures/base';

test.describe('Create a booking', () => {
  test('member can book an available time slot', async ({ memberPortal, bookingPage }) => {
    await memberPortal.goToBooking();
    const slot = await bookingPage.findAvailableSlot();
    await bookingPage.selectSlot(slot);

    // Only test-specific assertions live in the test
    const dialog = bookingPage.confirmationDialog;
    await expect(dialog.locator('text=/\\d+ kr|Gratis/')).toBeVisible();

    await bookingPage.confirmBooking();
  });
});
```

```ts
// tests/admin/blocks/resource-blocks.spec.ts
import { test, expect } from '../../../fixtures/admin';

test('admin can create a resource block', async ({ adminPage }) => {
  await adminPage.goto('/resources');
  // ... admin test logic
});
```

## Adding a New Feature's Tests

Step-by-step for adding E2E coverage to a new feature:

### Step 1: Decide IF this needs an E2E test

Ask yourself:
- Is this a critical user journey? (booking, auth, payment)
- Does it cross service boundaries that integration tests can't cover?
- Has it broken in production before?

If not, write a component or API test instead.

### Step 2: Add data-testid to React components

```tsx
// In your React component
<Dialog data-testid="invoice-dialog" open={open}>
  <Button data-testid="invoice-send-btn" onClick={handleSend}>
    Skicka faktura
  </Button>
</Dialog>
```

Only add `data-testid` where `getByRole` or `getByLabel` won't work cleanly.

### Step 3: Create a Page Object

```ts
// pages/invoice.page.ts
import { type Page, type Locator, expect } from '@playwright/test';

export class InvoicePage {
  readonly page: Page;
  readonly sendDialog: Locator;
  readonly sendButton: Locator;

  constructor(page: Page) {
    this.page = page;
    this.sendDialog = page.getByTestId('invoice-dialog');
    this.sendButton = page.getByTestId('invoice-send-btn');
  }

  async sendInvoice() {
    await expect(this.sendButton).toBeEnabled();

    // Intercept API response for reliable assertion
    const responsePromise = this.page.waitForResponse(
      (r) => r.url().includes('/api/invoices') && r.request().method() === 'POST',
    );

    await this.sendButton.click();

    const response = await responsePromise;
    if (!response.ok()) {
      const body = await response.json();
      throw new Error(`Send invoice failed: ${body.error ?? body.title}`);
    }

    await expect(this.page.getByText(/faktura skickad/i)).toBeVisible();
  }
}
```

### Step 4: Register in fixtures

```ts
// fixtures/base.ts — add to the existing extend call
import { InvoicePage } from '../pages/invoice.page';

export const test = base.extend<{
  memberPortal: MemberPortalPage;
  bookingPage: BookingPage;
  invoicePage: InvoicePage;  // Add here
}>({
  // ... existing fixtures ...
  invoicePage: async ({ page }, use) => {
    await use(new InvoicePage(page));
  },
});
```

### Step 5: Write the test

```ts
// tests/invoices/send-invoice.spec.ts
import { test, expect } from '../../fixtures/base';

test('admin can send an invoice', async ({ memberPortal, invoicePage }) => {
  await memberPortal.goToInvoices();
  await invoicePage.sendInvoice();
});
```

### Step 6: If needed, add seed data or per-test API setup

**For shared baseline data** — extend `SeedE2eTest.cs`:
```csharp
var invoice = new Invoice { Id = Guid.Parse("..."), /* ... */ };
if (!await db.Invoices.AnyAsync(i => i.Id == invoice.Id))
    db.Invoices.Add(invoice);
```

**For test-specific data** — use API setup in the test:
```ts
test('cancel a pending invoice', async ({ request, memberPortal, invoicePage }) => {
  // Create invoice via API (fast, isolated)
  const res = await request.post('/api/invoices', {
    data: { amount: 500, customerId: testCustomerId }
  });
  const { id } = await res.json();

  // Test the cancellation UI
  await memberPortal.goToInvoices();
  await invoicePage.cancelInvoice(id);
});
```
