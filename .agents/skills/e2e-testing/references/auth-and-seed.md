# Auth Setup & Seed Data

## Table of Contents
- [Overview](#overview)
- [Test Users](#test-users)
- [Seed Data Architecture](#seed-data-architecture)
- [Auth Setup File](#auth-setup-file)
- [Playwright Config Projects](#playwright-config-projects)
- [Per-Test API Data Setup](#per-test-api-data-setup)
- [Adding New Seed Data](#adding-new-seed-data)

## Overview

E2E tests need two things before they run:
1. **Seed data** — dev seed users with known credentials and roles
2. **Authentication** — saved browser state with auth cookies (super-admin + regular user)

Both happen in `auth.setup.ts`, which runs before all test files via Playwright's `dependencies` config.

## Test Users

From `DevSeedData.cs` — these users exist automatically in dev:

| Email | Password | OTP | Role | Auth State File |
|-------|----------|-----|------|-----------------|
| `admin@test.com` | `Test123!` | `111111` | SuperAdmin | `.auth/super-admin.json` |
| `user@test.com` | `Test123!` | `222222` | Regular user | `.auth/regular-user.json` |
| `test@test.com` | `Test123!` | `123456` | SuperAdmin | — (used for UI login tests) |

**Auth methods:** All users support both password login (`POST /api/auth/login`) and OTP login (`POST /api/auth/verify-otp`). For E2E setup, use password login (simpler, single request).

## Seed Data Architecture

Backend: `Infrastructure/DevSeed/DevSeedService.cs`

The dev seed service runs on startup in Development mode and creates:
- Three test users with known passwords and OTP codes
- Role assignments (SuperAdmin for admin@test.com and test@test.com)

**This happens automatically** — no explicit seed endpoint needed. The dev seed runs every time the API starts in development mode.

## Auth Setup File

```ts
// auth.setup.ts — runs BEFORE all tests
import { test as setup, expect } from '@playwright/test';
import path from 'path';

const SUPER_ADMIN_AUTH_FILE = path.join(__dirname, '.auth/super-admin.json');
const REGULAR_USER_AUTH_FILE = path.join(__dirname, '.auth/regular-user.json');

// Step 1: Health check — wait for backend to be ready
setup('wait for backend', async ({ request }) => {
  let healthy = false;
  for (let i = 0; i < 30; i++) {
    try {
      const res = await request.get('/health');
      if (res.ok()) { healthy = true; break; }
    } catch { /* retry */ }
    await new Promise(r => setTimeout(r, 1000));
  }
  expect(healthy, 'Backend did not become healthy within 30s').toBeTruthy();
});

// Step 2: Authenticate as super-admin and save browser state
setup('authenticate as super-admin', async ({ page }) => {
  // Navigate first to set the origin for cookies
  await page.goto('/');
  await page.waitForLoadState('domcontentloaded');

  // Auth via page.evaluate (sets HttpOnly cookie in browser context)
  const authResult = await page.evaluate(async () => {
    const res = await fetch(window.location.origin + '/api/auth/login', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ email: 'admin@test.com', password: 'Test123!' })
    });
    return { ok: res.ok, status: res.status };
  });
  expect(authResult.ok).toBeTruthy();

  await page.reload();
  // Wait for auth to be established (app renders authenticated content)
  await page.waitForLoadState('domcontentloaded');

  // Save auth state for all subsequent super-admin tests
  await page.context().storageState({ path: SUPER_ADMIN_AUTH_FILE });
});

// Step 3: Authenticate as regular user and save browser state
setup('authenticate as regular user', async ({ page }) => {
  await page.goto('/');
  await page.waitForLoadState('domcontentloaded');

  const authResult = await page.evaluate(async () => {
    const res = await fetch(window.location.origin + '/api/auth/login', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ email: 'user@test.com', password: 'Test123!' })
    });
    return { ok: res.ok, status: res.status };
  });
  expect(authResult.ok).toBeTruthy();

  await page.reload();
  await page.waitForLoadState('domcontentloaded');

  // Save auth state for all subsequent regular-user tests
  await page.context().storageState({ path: REGULAR_USER_AUTH_FILE });
});
```

**Why `page.evaluate` for auth?** The API uses HttpOnly cookies (`auth-token`). `request.post` doesn't set cookies in the browser context. By calling `fetch()` from inside `page.evaluate`, the cookie is set in the actual browser's cookie jar.

**Why password login instead of OTP?** Both work, but password login is a single request (`POST /api/auth/login`) vs two for OTP (`POST /api/auth/send-otp` → `POST /api/auth/verify-otp`). Simpler and faster.

**Why `domcontentloaded` and NOT `networkidle`?** `networkidle` waits for "no network activity for 500ms" which fails with SignalR WebSockets, analytics pings, or any persistent connection. `domcontentloaded` is sufficient here because we only need the page origin to be set before calling `page.evaluate`.

## Playwright Config Projects

```ts
// playwright.config.ts
projects: [
  // 1. Setup runs first: health check + saves auth state
  {
    name: 'setup',
    testDir: '.',
    testMatch: /auth\.setup\.ts/,
  },
  // 2. Auth tests: run after setup, NO saved auth (tests handle their own login)
  {
    name: 'auth-tests',
    testMatch: /auth\/.*\.spec\.ts/,
    dependencies: ['setup'],
    // No storageState — tests start as unauthenticated
  },
  // 3. Unauthenticated tests: run after setup, NO saved auth
  {
    name: 'unauthenticated-tests',
    testMatch: /unauthenticated\/.*\.spec\.ts/,
    dependencies: ['setup'],
    // No storageState — tests start as unauthenticated
  },
  // 4. Super-admin tests: run after setup, WITH saved super-admin auth
  {
    name: 'super-admin-tests',
    testMatch: /super-admin\/.*\.spec\.ts/,
    use: { storageState: '.auth/super-admin.json' },
    dependencies: ['setup'],
  },
  // 5. Regular-user tests: run after setup, WITH saved regular-user auth
  {
    name: 'regular-user-tests',
    testMatch: /regular-user\/.*\.spec\.ts/,
    use: { storageState: '.auth/regular-user.json' },
    dependencies: ['setup'],
  },
]
```

**Execution order:**
```
setup (health check + 2 auth sequences)
  ↓
├─ auth-tests             (tests/auth/*, no storageState)
├─ unauthenticated-tests  (tests/unauthenticated/*, no storageState)
├─ super-admin-tests      (tests/super-admin/*, storageState: super-admin)
└─ regular-user-tests     (tests/regular-user/*, storageState: regular-user)
```

## Per-Test API Data Setup

**Global seed = dev seed users with known credentials and roles (auto-seeded on startup).**

**Per-test API setup = data specific to what THIS test verifies.** This pattern enables test isolation and future parallel execution.

```ts
test('can delete an entity', async ({ request, page }) => {
  // Create test data via API (specific to this test)
  const createRes = await request.post('/api/some-entity', {
    data: { name: 'Test Entity for Deletion' }
  });
  expect(createRes.ok()).toBeTruthy();
  const { id } = await createRes.json();

  // Test the actual UI flow
  await page.goto('/super-admin/some-entity');
  await page.getByTestId(`entity-${id}`).click();
  await page.getByRole('button', { name: 'Delete' }).click();
  await expect(page.getByText('Deleted successfully')).toBeVisible();
});
```

**Benefits over shared seed data:**
- Tests don't interfere with each other
- Tests can run in parallel
- Failures are isolated — one test's data doesn't corrupt another
- Tests are self-documenting — you can see exactly what data they need
- No cleanup needed — DB is thrown away after CI run

## Adding New Seed Data

When a new feature needs **baseline data that all tests share**:

1. **Extend** `DevSeedData.cs` and `DevSeedService.cs`
2. **Keep it idempotent** — check if data exists before creating
3. **Return deterministic data** — same seed always produces same state
4. **Don't clean up** — seed data persists, DB is disposable

When a test needs **specific data only it cares about** — use per-test API setup instead (see above).
