import { defineConfig } from '@playwright/test'
import { mkdtempSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

// Defense in depth: also suppress automatic failure DOM snapshots when the
// runner is invoked directly rather than through the .NET orchestrator.
process.env.PLAYWRIGHT_NO_COPY_PROMPT = '1'

// Launched only by LiveFixBrowserTests against its loopback Kestrel host and
// fresh postgres-dev database. No existing E2E setup, teardown or Docker rig.
export default defineConfig({
  testDir: './tests/live',
  workers: 1,
  retries: 0,
  timeout: 60_000,
  expect: { timeout: 10_000 },
  reporter: 'list',
  preserveOutput: 'never',
  outputDir: mkdtempSync(join(tmpdir(), 'modgud-live-fixes-')),
  use: {
    browserName: 'chromium',
    serviceWorkers: 'block',
    trace: 'off',
    screenshot: 'off',
    video: 'off',
  },
})
