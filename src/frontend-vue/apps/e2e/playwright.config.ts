import { defineConfig, devices } from '@playwright/test';

const baseURL = process.env['BASE_URL'] || 'http://localhost:4200';

export default defineConfig({
  testDir: './src',
  fullyParallel: true,
  forbidOnly: !!process.env['CI'],
  retries: process.env['CI'] ? 2 : 0,
  workers: process.env['CI'] ? 1 : undefined,
  reporter: process.env['CI'] ? 'github' : 'html',

  use: {
    baseURL,
    trace: 'on-first-retry',
    screenshot: 'only-on-failure',
  },

  projects: [
    // Auth setup — runs first, saves auth state for other tests
    {
      name: 'auth-setup',
      testMatch: /auth\.setup\.ts/,
    },

    // Main tests — use saved auth state
    {
      name: 'chromium',
      use: {
        ...devices['Desktop Chrome'],
        storageState: 'playwright/.auth/admin.json',
      },
      dependencies: ['auth-setup'],
    },
  ],

  // Start frontend dev server if not already running
  webServer: {
    command: 'pnpm -C ../frontend dev',
    url: baseURL,
    reuseExistingServer: true,
    timeout: 30_000,
  },
});
