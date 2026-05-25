import { defineConfig } from '@playwright/test'

/**
 * Playwright config for the TestApps E2E suite.
 *
 * Topology:
 *
 *   Modgud      :9099  (must already be running, demo seed loaded)
 *   ResourceApi      :7081  (started by webServer below)
 *   BFF              :7080  (started by webServer below)
 *
 * Pre-conditions to running this suite:
 *   1. Modgud is reachable at TESTAPPS_AUTHORITY (default localhost:9099)
 *   2. The /setup wizard has been completed AND demo-seed.json has been
 *      imported (admin runs /setup with LoadDemoData=true). The seed
 *      provides demo-bff (BFF client), demo-backend (M2M client),
 *      demo-api (resource), demo.read/write/admin scopes and the
 *      `demo.admin` user with password Demo1234!.
 *   3. .NET 10 SDK on PATH so dotnet run can start ResourceApi + BFF.
 *
 * The suite never seeds/resets the auth DB itself — that's deliberately
 * the integration-test rig's job. We only exercise the test apps.
 */

const AUTHORITY = process.env.TESTAPPS_AUTHORITY ?? 'http://localhost:9099'
const RESOURCEAPI_URL = process.env.TESTAPPS_RESOURCEAPI_URL ?? 'http://localhost:7081'
const BFF_URL = process.env.TESTAPPS_BFF_URL ?? 'http://localhost:7080'

export default defineConfig({
  testDir: '.',
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: 'list',
  timeout: 30_000,
  expect: { timeout: 5_000 },
  use: {
    baseURL: BFF_URL,
    trace: 'on-first-retry',
    extraHTTPHeaders: {
      // Most BFF endpoints require it. Specs that test the guard itself
      // override the headers per-request.
      'X-Requested-With': 'XMLHttpRequest',
    },
  },
  projects: [
    { name: 'chromium', use: { browserName: 'chromium' } },
  ],
  webServer: [
    {
      command:
        `dotnet run --project ../src/dotnet/TestApps/Modgud.TestApps.ResourceApi --no-launch-profile`,
      env: {
        ASPNETCORE_URLS: RESOURCEAPI_URL,
        'TESTAPPS__AUTHORITY': AUTHORITY,
        'TESTAPPS__AUDIENCE': 'demo-api',
      },
      url: `${RESOURCEAPI_URL}/health`,
      reuseExistingServer: !process.env.CI,
      timeout: 120_000,
      stdout: 'pipe',
      stderr: 'pipe',
    },
    {
      command:
        `dotnet run --project ../src/dotnet/TestApps/Modgud.TestApps.Bff --no-launch-profile`,
      env: {
        ASPNETCORE_URLS: BFF_URL,
        'TESTAPPS__AUTHORITY': AUTHORITY,
        'TESTAPPS__RESOURCEAPI': RESOURCEAPI_URL,
        'TESTAPPS__CLIENTID': 'demo-bff',
        'TESTAPPS__CLIENTSECRET': 'demo-bff-secret-please-rotate',
      },
      url: `${BFF_URL}/health`,
      reuseExistingServer: !process.env.CI,
      timeout: 120_000,
      stdout: 'pipe',
      stderr: 'pipe',
    },
  ],
})
