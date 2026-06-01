import { test as setup, expect } from '@playwright/test';
import path from 'path';
import { fileURLToPath } from 'url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const SUPER_ADMIN_AUTH = path.join(__dirname, '.auth/super-admin.json');
const REGULAR_USER_AUTH = path.join(__dirname, '.auth/regular-user.json');

setup('wait for backend', async ({ request }) => {
  let healthy = false;
  for (let i = 0; i < 30; i++) {
    try {
      const res = await request.get('/health');
      if (res.ok()) {
        healthy = true;
        break;
      }
    } catch {
      // Server not ready yet
    }
    await new Promise((r) => setTimeout(r, 1000));
  }
  expect(healthy, 'Backend did not become healthy within 30s').toBeTruthy();
});

setup('authenticate as super-admin', async ({ page }) => {
  await page.goto('/');
  await page.waitForLoadState('domcontentloaded');

  const authResult = await page.evaluate(async () => {
    const res = await fetch(window.location.origin + '/api/auth/login', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ email: 'admin@test.com', password: 'Test123!' }),
    });
    return { ok: res.ok, status: res.status };
  });
  expect(authResult.ok, `Super-admin login failed with status ${authResult.status}`).toBeTruthy();

  // Reload to let the app recognize the auth cookie
  await page.reload();
  await page.waitForLoadState('domcontentloaded');

  await page.context().storageState({ path: SUPER_ADMIN_AUTH });
});

setup('authenticate as regular-user', async ({ page }) => {
  await page.goto('/');
  await page.waitForLoadState('domcontentloaded');

  const authResult = await page.evaluate(async () => {
    const res = await fetch(window.location.origin + '/api/auth/login', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ email: 'user@test.com', password: 'Test123!' }),
    });
    return { ok: res.ok, status: res.status };
  });
  expect(authResult.ok, `Regular-user login failed with status ${authResult.status}`).toBeTruthy();

  await page.reload();
  await page.waitForLoadState('domcontentloaded');

  await page.context().storageState({ path: REGULAR_USER_AUTH });
});
