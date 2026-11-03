import { defineConfig } from '@playwright/test'
import { mkdtempSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

// Deliberately separate from playwright.config.ts: no global setup/teardown,
// web server, external baseURL, backend or container lifecycle.
export default defineConfig({
  testDir: './tests/browser',
  workers: 1,
  retries: 0,
  timeout: 30_000,
  expect: { timeout: 5_000 },
  reporter: 'list',
  outputDir: mkdtempSync(join(tmpdir(), 'modgud-isolated-browser-')),
  use: {
    browserName: 'chromium',
    serviceWorkers: 'block',
  },
})
