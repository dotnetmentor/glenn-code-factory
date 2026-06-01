import { defineConfig } from '@playwright/test';

export default defineConfig({
  testDir: './tests',
  fullyParallel: false,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 2 : 1,
  workers: 1,
  maxFailures: process.env.CI ? 5 : undefined,
  reporter: [
    ['html', { open: 'never' }],
    ['list'],
  ],
  use: {
    baseURL: 'http://localhost:5173',
    trace: 'on-first-retry',
    screenshot: 'only-on-failure',
  },
  projects: [
    {
      name: 'setup',
      testDir: '.',
      testMatch: /auth\.setup\.ts/,
    },
    {
      name: 'auth-tests',
      testMatch: /auth\/.*\.spec\.ts/,
      dependencies: ['setup'],
    },
    {
      name: 'unauthenticated-tests',
      testMatch: /unauthenticated\/.*\.spec\.ts/,
      dependencies: ['setup'],
    },
    {
      name: 'super-admin-tests',
      testMatch: /super-admin\/.*\.spec\.ts/,
      use: { storageState: '.auth/super-admin.json' },
      dependencies: ['setup'],
    },
    {
      name: 'regular-user-tests',
      testMatch: /regular-user\/.*\.spec\.ts/,
      use: { storageState: '.auth/regular-user.json' },
      dependencies: ['setup'],
    },
  ],
});
