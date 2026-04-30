import { defineConfig } from '@playwright/test'

export default defineConfig({
  globalSetup: './e2e/global-setup.ts',
  globalTeardown: './e2e/global-teardown.ts',
  testDir: './e2e',
  // _legacy/ holds pre-cutover specs preserved as patterns; they're not
  // wired up to the new rig and don't run.
  testIgnore: ['**/_legacy/**'],
  timeout: 30_000,
  expect: { timeout: 5_000 },
  fullyParallel: false, // Sequential — shared backend state
  workers: 1,          // Single worker — tests modify shared user state
  retries: 0,
  reporter: 'list',
  use: {
    // Dynamic baseURL from Testcontainers — set in global-setup via env var
    baseURL: process.env.E2E_BASE_URL || 'http://localhost:8081',
    trace: 'on-first-retry',
  },
  projects: [
    {
      name: 'chromium',
      use: {
        browserName: 'chromium',
      },
    },
  ],
})
