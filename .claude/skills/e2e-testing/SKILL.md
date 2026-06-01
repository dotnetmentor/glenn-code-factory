---
name: e2e-testing
description: Write resilient Playwright E2E tests using Page Object Models, web-first assertions, and API response interception. Use when (1) writing new E2E tests, (2) adding test coverage for a feature, (3) fixing flaky or broken E2E tests, (4) adding data-testid attributes to frontend components for testability, (5) user says "e2e", "end-to-end", "playwright", "write a test", "test this feature". Covers selector strategy, waiting patterns, POM architecture, auth setup, seed data, test scope decisions, and anti-patterns to avoid.
---

# E2E Testing with Playwright

## Project Structure

```
packages/e2e/
├── playwright.config.ts    # 5 projects: setup, auth, unauthenticated, super-admin, regular-user
├── auth.setup.ts           # Health check + authenticate users (super-admin + regular-user)
├── fixtures/
│   ├── base.ts             # Super-admin POM fixtures (import this, not @playwright/test)
│   └── regular-user.ts     # Regular user POM fixtures
├── pages/                  # Page Object Models
│   ├── login.page.ts
│   └── dashboard.page.ts
└── tests/
    ├── auth/               # Tests that handle their own auth (no saved state)
    ├── unauthenticated/    # Unauthenticated user tests (redirects, guards)
    ├── super-admin/        # Super-admin feature tests
    └── regular-user/       # Regular user feature tests
```

## Running Tests

```bash
cd packages/e2e
npx playwright test              # headless
npx playwright test --headed     # visible browser
npx playwright test --ui         # interactive UI mode
npx playwright show-report       # view last report with traces
```

## When to Write E2E Tests (And When NOT To)

E2E tests are expensive to write, slow to run, and costly to maintain. Use them only where lower-level tests can't give you the same confidence.

**Write E2E for:**
- Critical user journeys (booking a slot, signing in, completing a purchase)
- Flows that cross multiple services/boundaries (auth + API + UI)
- Revenue-critical paths where a failure = user can't do their job
- Flows that have broken in production before

**DON'T write E2E for:**
- Form validation ("email shows error") — component/unit test
- Sorting, filtering, pagination — component test
- Permission checks ("non-admin can't see admin page") — API test
- CRUD for every entity — test one representative flow e2e, rest via API
- Error states and edge cases — component test with mocked data
- Third-party integrations — mock the boundary

**The rule:** If the same confidence can be achieved with a unit or integration test, write that instead.

## Core Rules

### 1. NEVER Use `waitForTimeout` or `networkidle`

```ts
// ❌ NEVER — arbitrary delay, slow AND flaky
await page.waitForTimeout(2_000);

// ❌ NEVER in SPAs — waits for "no network for 500ms", breaks with
// SignalR WebSockets, analytics pings, health checks, long-polling
await page.waitForLoadState('networkidle');

// ✅ Web-first assertion — auto-retries until true or timeout
await expect(page.getByTestId('booking-success-title')).toBeVisible();

// ✅ Wait for specific network response (precise signal)
const responsePromise = page.waitForResponse(r =>
  r.url().includes('/api/bookings') && r.request().method() === 'POST'
);
await page.getByTestId('booking-confirm-btn').click();
const response = await responsePromise;

// ✅ Wait for a loading transition to complete
const loaded = calendarGrid.or(page.getByText('Inga lediga tider'));
await expect(loaded).toBeVisible();

// ✅ Wait for navigation label to change (proves nav happened)
const currentLabel = await weekLabel.textContent();
await weekNavNext.click();
await expect(weekLabel).not.toHaveText(currentLabel ?? '', { timeout: 5_000 });
```

### 2. Selector Priority

Use in this order. Fall to next level only when the higher one doesn't work.

| Priority | Selector | When | Why |
|----------|----------|------|-----|
| 1st | `getByRole('button', { name: 'Save' })` | Interactive elements (buttons, links, headings) | Validates accessibility, resilient to refactors |
| 2nd | `getByLabel('Email')` | Form fields with labels | User-facing, semantic |
| 3rd | `getByText('Welcome')` | Asserting non-interactive content | Readable |
| 4th | `getByTestId('nav-tab-book')` | Structural/navigation elements, containers, dynamic lists | Stable contract when roles/text are ambiguous |
| Never | `locator('.css-class')` | Never. Add a `data-testid` instead | Breaks on any styling change |

**When `getByTestId` beats `getByRole`:**
- Navigation tabs in a Swedish/multilingual UI (text changes per locale)
- Container/wrapper elements with no semantic role
- Dynamic lists: `data-testid={\`card-${item.id}\`}`
- Elements where multiple matches exist for the same role+name

### 3. API Response Interception — The Gold Standard

For any action that triggers an API call, **intercept the response before clicking**. This is the most reliable pattern for CRUD operations — it eliminates the gap between "API done" and "DOM updated."

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

  // 4. Only THEN assert on DOM (which should now be updated)
  await expect(this.successTitle).toBeVisible();
}
```

**Why this matters:** Without API interception, you're guessing whether the DOM update reflects a successful API call or a stale state. With it, you know the API succeeded before checking the UI.

### 4. Always Use Web-First Assertions

```ts
// ❌ WRONG — checks once, returns false immediately if not visible
expect(await element.isVisible()).toBe(true);
const text = await element.textContent();
expect(text).toBe('Expected');

// ✅ CORRECT — auto-retries until timeout (default 30s)
await expect(element).toBeVisible();
await expect(element).toHaveText('Expected');
await expect(element).toBeEnabled();
await expect(element).not.toBeVisible();  // waits for disappearance
```

### 5. Use `expect.soft()` for Multi-Assertion Tests

When verifying multiple things on a page, a hard assertion on item 2 skips items 3-5. Use soft assertions to collect ALL failures:

```ts
// ❌ First failure stops test — you miss other failures
await expect(page.getByText('Court 1')).toBeVisible();
await expect(page.getByText('60 min')).toBeVisible();  // fails here
await expect(page.getByText('200 kr')).toBeVisible();  // never checked

// ✅ Reports ALL failures at once
await expect.soft(page.getByText('Court 1')).toBeVisible();
await expect.soft(page.getByText('60 min')).toBeVisible();
await expect.soft(page.getByText('200 kr')).toBeVisible();
```

### 6. Always Use Page Object Models

Tests import from `fixtures/base.ts` (member) or `fixtures/admin.ts` (admin), never from `@playwright/test` directly (except auth/guest tests).

```ts
// ✅ Test reads like English — POM handles complexity
import { test, expect } from '../../fixtures/base';

test('member can book a slot', async ({ memberPortal, bookingPage }) => {
  await memberPortal.goToBooking();
  const slot = await bookingPage.findAvailableSlot();
  await bookingPage.selectSlot(slot);
  await bookingPage.confirmBooking();
});
```

```ts
// ✅ Admin tests use admin fixture
import { test, expect } from '../../fixtures/admin';

test('admin can create a block', async ({ adminPage }) => {
  await adminPage.goto('/resources');
  // ...
});
```

### 7. Per-Test Data Setup via API

For tests that create/modify data, set up test-specific data via API instead of relying on shared seed data. This enables test isolation and future parallel execution.

```ts
test('cancel a booking', async ({ request, page }) => {
  // Create test data via API — fast, isolated, doesn't affect other tests
  const booking = await request.post('/api/resource-bookings', {
    data: { resourceId, startTime, endTime }
  });
  expect(booking.ok()).toBeTruthy();
  const { id } = await booking.json();

  // Now test the actual UI flow
  await page.goto(`/members/e2e-test/bookings`);
  await page.getByTestId(`booking-${id}`).click();
  await page.getByRole('button', { name: 'Cancel' }).click();
  await expect(page.getByText('Booking cancelled')).toBeVisible();
});
```

**Global seed = baseline data that every test needs (org, resources, users).**
**Per-test API setup = data specific to what THIS test is verifying.**

### 8. Adding data-testid to Frontend Components

When writing E2E tests for a new feature, first add `data-testid` to the React components.

**Add testid to:**
- Navigation elements (tabs, menus, breadcrumbs)
- Dialogs and their action buttons
- Cards and list items (use dynamic IDs: `` data-testid={`card-${item.id}`} ``)
- Navigation arrows/pagination
- Success/error states
- Calendar grids and structural containers

**Skip testid for:**
- Simple buttons with clear accessible names (use `getByRole` instead)
- Form inputs with labels (use `getByLabel` instead)
- Headings (use `getByRole('heading', { name: ... })`)

**Convention:** `kebab-case`, descriptive: `booking-confirm-btn`, `nav-tab-home`, `week-nav-next`

### 9. Writing a Page Object Model

See [references/pom-pattern.md](references/pom-pattern.md) for the full POM + fixture pattern with examples.

Key rules:
- Store locators as `readonly` properties in constructor
- Use `getByRole` for interactive elements, `getByTestId` for structural ones
- Methods should wait for the result (web-first assertions inside the POM)
- Use API response interception for any method that triggers an API call
- Wrap POMs in fixtures in `fixtures/base.ts` or `fixtures/admin.ts`

### 10. Auth Setup Pattern

See [references/auth-and-seed.md](references/auth-and-seed.md) for the full auth and seed data pattern.

Key points:
- `auth.setup.ts` runs before all tests (seeds data + authenticates member + admin)
- Seed endpoint is idempotent (201 created, 409 already exists)
- Member auth saved to `.auth/member.json`, admin to `.auth/admin.json`
- Auth tests go in `tests/auth/` and use base Playwright test (no saved state)
- Guest tests go in `tests/guest/` (no saved state)
- Feature tests import from `fixtures/base.ts` (member) or `fixtures/admin.ts` (admin)

### 11. Anti-Patterns Cheat Sheet

| Anti-Pattern | Fix |
|---|---|
| `waitForTimeout(N)` | `await expect(locator).toBeVisible()` — auto-retries |
| `waitForLoadState('networkidle')` | Wait for a specific signal: element visible, API response, label change |
| `expect(await loc.isVisible()).toBe(true)` | `await expect(loc).toBeVisible()` — web-first, retries |
| `page.locator('.btn-primary')` | `page.getByRole('button', { name: '...' })` |
| `page.locator('#id > div > button')` | Add `data-testid` and use `getByTestId` |
| `getByRole('listitem').nth(2)` | `.filter({ hasText: 'Name' })` — readable + stable |
| Inline navigation logic in tests | POM method: `portal.goToBooking()` |
| `import { test } from '@playwright/test'` | `import { test } from '../../fixtures/base'` |
| Text-dependent nav in multilingual UI | `getByTestId('nav-tab-book')` for nav, `getByRole` for buttons |
| Asserting after click without API check | Intercept API response first, then assert DOM |
| Creating test data through UI clicks | API setup: `request.post('/api/...')` — 10-100x faster |
| One giant 200-line test | Small focused tests, each setting up its own data |
| Testing form validation e2e | Component/unit test — e2e is for user journeys |
| Serial test dependencies (B needs A's data) | Each test creates its own data via API |

### 12. CI/CD Tips

```ts
// playwright.config.ts — CI optimizations
export default defineConfig({
  maxFailures: process.env.CI ? 5 : undefined,  // Stop cascade failures early
  retries: process.env.CI ? 2 : 1,
  workers: 1,  // Keep at 1 until tests are fully isolated
});
```

**CI workflow tips:**
- Only install Chromium: `npx playwright install chromium --with-deps`
- Upload `playwright-report/` as artifact on ALL outcomes (not just failure)
- Upload `test-results/` (traces, screenshots) on failure only
- Use `cancel-in-progress: true` concurrency to save CI minutes
- Set `maxFailures` to avoid burning CI time on cascade failures

### 13. Debugging Flaky Tests

When a test is flaky:

1. **Check the trace** — `npx playwright show-report` → click the test → view trace
2. **Look for missing wait signals** — Are you waiting for the right thing?
3. **Check for `networkidle`** — Replace with specific element/response waits
4. **Check for animation interference** — Consider disabling animations:
   ```ts
   await page.addStyleTag({
     content: '*, *::before, *::after { transition: none !important; animation: none !important; }'
   });
   ```
5. **Check for shared state** — Does this test depend on another test's data?
6. **Never ignore flaky tests** — A flaky test is a test that sometimes lies to you. Fix it or delete it.
